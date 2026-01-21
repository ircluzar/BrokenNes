using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Threading.Tasks;
using BrokenNes;
using BrokenNes.CorruptorModels;
using NesEmulator;
using NesEmulator.Shaders;
using BrokenNes.Windows.Rendering;
using BrokenNes.Windows.Tools;
using PngPayloadEmbedding;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace BrokenNes.Windows
{
    public enum ViewMode
    {
        Emulator,  // Only emulator, no WebView2
        Widget,    // Emulator + WebView2 side by side
        Web,       // Only WebView2, emulator hidden
        Overlay    // WebView2 transparent overlay on top of emulator
    }
    
    public partial class MainForm : Form
    {
        private NES? nes;
        private Thread? emulatorThread;
        private volatile bool isEmulationRunning;
        private volatile bool isPaused;
        private readonly object emulationLock = new object();
        private NesDirectXRenderer dxRenderer;
        private Panel displayPanel;
        private DirectBitmap? frameBuffer;
        private DirectBitmap? backBuffer; // Double buffering
        private AudioManager? audioManager;
        private string currentRomPath = string.Empty;
        private EmulatorConfig config = new EmulatorConfig();
        private bool useDirectX = true;
        private string? quickSaveState; // Quick save slot
        private InputManager? inputManager;
        private InputManager? inputManager2; // Player 2 input
        
        // FPS tracking for audio speed adjustment
        private double currentFps = 60.0;
        private int fpsFrameCount = 0;
        private System.Diagnostics.Stopwatch? fpsStopwatch;
        
        // Speed control
        private SpeedControlForm? speedControlForm;
        private volatile float speedOverride = 1.0f;
        private volatile bool hasSpeedOverride = false;
        private volatile bool resetTimingAccumulator = false;
        
        // Auto-scramble cores testing
        private System.Windows.Forms.Timer? autoScrambleTimer;
        private Random scrambleRandom = new Random();
        
        // Continue feature
        private PictureBox? continueButton;

        // Menu items
        private ToolStripMenuItem shaderMenu;
        private ToolStripMenuItem apuMenu;
        private ToolStripMenuItem cpuMenu;
        private ToolStripMenuItem ppuMenu;
        private ToolStripMenuItem recentRomsMenu;
        
        // NES display dimensions
        private const int NES_WIDTH = 256;
        private const int NES_HEIGHT = 240;
        
        // Input mapping
        private Dictionary<Keys, int> keyMap = new();

        // Corruptor + Imagine
        private readonly Corruptor corruptor = new();
        private readonly object corruptorLock = new();
        private ImagineEngine? imagineEngine;
        private RealTimeCorruptorForm? rtcForm;
        private GlitchHarvesterForm? ghForm;
        private ImagineForm? imagineForm;
        private HexEditorForm? hexEditorForm;
        private readonly ConcurrentQueue<Action> emulationActions = new();
        public event Action? CorruptorStateChanged;
        
        // Fullscreen support
        private bool isFullscreen = false;
        private FormBorderStyle previousBorderStyle;
        private FormWindowState previousWindowState;
        private Rectangle previousBounds;
        
        // WebView2 and view modes
        private WebView2? webView;
        private ViewMode currentViewMode = ViewMode.Emulator;
        private bool isWebViewInitialized = false;
        
        /// <summary>
        /// Get the effective menu height - returns 0 if menu is hidden (fullscreen + HideMenuBarInFullscreen)
        /// </summary>
        private int GetEffectiveMenuHeight()
        {
            if (this.MainMenuStrip != null && this.MainMenuStrip.Visible)
            {
                return this.MainMenuStrip.Height;
            }
            return 0;
        }
        
        /// <summary>
        /// Toggle between fullscreen and windowed mode
        /// </summary>
        private void ToggleFullscreen()
        {
            if (isFullscreen)
            {
                // Restore menu bar visibility first
                if (this.MainMenuStrip != null)
                {
                    this.MainMenuStrip.Visible = true;
                }
                
                // Exit fullscreen
                this.FormBorderStyle = previousBorderStyle;
                this.WindowState = previousWindowState;
                this.Bounds = previousBounds;
                isFullscreen = false;
            }
            else
            {
                // Enter fullscreen
                previousBorderStyle = this.FormBorderStyle;
                previousWindowState = this.WindowState;
                previousBounds = this.Bounds;
                
                this.FormBorderStyle = FormBorderStyle.None;
                this.WindowState = FormWindowState.Normal;
                this.Bounds = Screen.FromControl(this).Bounds;
                isFullscreen = true;
                
                // Hide menu bar if configured
                if (this.MainMenuStrip != null && config.HideMenuBarInFullscreen)
                {
                    this.MainMenuStrip.Visible = false;
                }
            }
            
            // Re-apply the current view mode to recalculate layout with new menu visibility
            SwitchViewMode(currentViewMode);
            
            // Force layout and rendering refresh
            this.PerformLayout();
            displayPanel?.PerformLayout();
            dxRenderer?.Invalidate();
            this.Refresh();
        }
        
        /// <summary>
        /// Initialize WebView2 asynchronously
        /// </summary>
        private async void InitializeWebViewAsync()
        {
            if (webView == null) return;
            
            try
            {
                await webView.EnsureCoreWebView2Async(null);
                
                // Enable transparency for overlay mode
                webView.DefaultBackgroundColor = Color.Transparent;
                
                isWebViewInitialized = true;
                Console.WriteLine("WebView2 initialized successfully with transparency");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebView2 initialization error: {ex.Message}");
                MessageBox.Show($"Failed to initialize WebView2: {ex.Message}", 
                    "WebView2 Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        
        /// <summary>
        /// Switch between view modes (Emulator, Widget, Web)
        /// </summary>
        private async void SwitchViewMode(ViewMode mode)
        {
            if (webView == null)
            {
                MessageBox.Show("WebView2 is not available.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            
            // Wait for WebView2 to be initialized if switching to Widget, Overlay or Web mode
            if ((mode == ViewMode.Widget || mode == ViewMode.Overlay || mode == ViewMode.Web) && !isWebViewInitialized)
            {
                MessageBox.Show("WebView2 is still initializing. Please try again in a moment.", 
                    "Please Wait", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            
            currentViewMode = mode;
            
            // Suspend layout during control rearrangement
            this.SuspendLayout();
            
            int menuHeight = GetEffectiveMenuHeight();
            int availableHeight = this.ClientSize.Height - menuHeight;
            
            switch (mode)
            {
                case ViewMode.Emulator:
                    // Only emulator visible, below menu bar
                    displayPanel.Visible = true;
                    displayPanel.Location = new Point(0, menuHeight);
                    displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                    webView.Visible = false;
                    
                    // Center the viewport
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.5f;
                    }
                    
                    Console.WriteLine("Switched to Emulator mode");
                    break;
                    
                case ViewMode.Widget:
                    // Widget mode - background renders full width, WebView2 panel on right side
                    // Display panel fills entire area (background visible everywhere)
                    displayPanel.Visible = true;
                    displayPanel.Location = new Point(0, menuHeight);
                    displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                    
                    // Calculate NES optimal width for the panel positioning
                    float nesAspectRatio = (float)NES_WIDTH / NES_HEIGHT;
                    int nesWidth = (int)(availableHeight * nesAspectRatio);
                    int maxNesWidth = (int)(this.ClientSize.Width * 0.75f);
                    if (nesWidth > maxNesWidth)
                    {
                        nesWidth = maxNesWidth;
                    }
                    
                    // Align NES viewport flush to the left
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.0f; // Flush left
                        
                        // Get actual viewport width from renderer (calculates on-demand)
                        var viewportRect = dxRenderer.GetViewportRect();
                        int actualViewportWidth = (int)Math.Ceiling(viewportRect.Right);
                        if (actualViewportWidth > 0 && actualViewportWidth < this.ClientSize.Width)
                        {
                            nesWidth = actualViewportWidth;
                        }
                    }
                    
                    // WebView2 overlays on the right side, flush to the viewport edge
                    webView.Visible = true;
                    webView.Location = new Point(nesWidth, menuHeight);
                    webView.Size = new Size(this.ClientSize.Width - nesWidth, availableHeight);
                    webView.BringToFront();
                    
                    // Load transparent HTML content with modal-like panel
                    if (isWebViewInitialized && webView.CoreWebView2 != null)
                    {
                        string htmlContent = $@"
                        <!DOCTYPE html>
                        <html>
                        <head>
                            <style>
                                body {{
                                    margin: 0;
                                    padding: 20px;
                                    background: transparent;
                                    font-family: 'Segoe UI', Arial, sans-serif;
                                    color: white;
                                    overflow: hidden;
                                    display: flex;
                                    align-items: stretch;
                                    height: calc(100vh - 40px);
                                    box-sizing: border-box;
                                }}
                                .widget-panel {{
                                    flex: 1;
                                    background: rgba(20, 20, 30, 0.85);
                                    backdrop-filter: blur(10px);
                                    display: flex;
                                    justify-content: center;
                                    align-items: center;
                                    flex-direction: column;
                                    border-radius: 16px;
                                    border: 2px solid rgba(255, 255, 255, 0.1);
                                    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
                                }}
                                .widget-content {{
                                    text-align: center;
                                    padding: 30px;
                                }}
                                h1 {{
                                    font-size: 32px;
                                    margin-bottom: 15px;
                                    font-weight: 600;
                                }}
                                p {{
                                    font-size: 16px;
                                    opacity: 0.8;
                                    line-height: 1.6;
                                }}
                            </style>
                        </head>
                        <body>
                            <div class='widget-panel'>
                                <div class='widget-content'>
                                    <h1>Widget Panel</h1>
                                    <p>Background renders underneath<br/>with transparent HTML overlay</p>
                                </div>
                            </div>
                        </body>
                        </html>";
                        
                        webView.CoreWebView2.NavigateToString(htmlContent);
                    }
                    Console.WriteLine($"Switched to Widget mode - Background full width, WebView panel width: {this.ClientSize.Width - nesWidth}px");
                    break;
                    
                case ViewMode.Overlay:
                    // Overlay mode - WebView2 transparent on top of emulator
                    displayPanel.Visible = true;
                    displayPanel.Location = new Point(0, menuHeight);
                    displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                    
                    // Center the viewport
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.5f;
                    }
                    
                    // WebView2 overlays the entire display panel
                    webView.Visible = true;
                    webView.Location = new Point(0, menuHeight);
                    webView.Size = new Size(this.ClientSize.Width, availableHeight);
                    webView.BringToFront(); // Ensure WebView2 is on top
                    
                    // Load HTML with transparent background and a floating box
                    if (isWebViewInitialized && webView.CoreWebView2 != null)
                    {
                        string htmlContent = @"
                        <!DOCTYPE html>
                        <html>
                        <head>
                            <style>
                                body {
                                    margin: 0;
                                    padding: 0;
                                    background: transparent;
                                    font-family: 'Segoe UI', Arial, sans-serif;
                                }
                                .floating-box {
                                    position: absolute;
                                    top: 50%;
                                    left: 50%;
                                    transform: translate(-50%, -50%);
                                    background: rgba(30, 144, 255, 0.9);
                                    color: white;
                                    padding: 30px 50px;
                                    border-radius: 15px;
                                    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
                                    text-align: center;
                                    font-size: 24px;
                                    font-weight: bold;
                                    backdrop-filter: blur(10px);
                                    border: 2px solid rgba(255, 255, 255, 0.3);
                                }
                                .subtitle {
                                    font-size: 14px;
                                    margin-top: 10px;
                                    opacity: 0.9;
                                    font-weight: normal;
                                }
                            </style>
                        </head>
                        <body>
                            <div class='floating-box'>
                                HTML Overlay
                                <div class='subtitle'>Floating over DirectX render</div>
                            </div>
                        </body>
                        </html>";
                        
                        webView.CoreWebView2.NavigateToString(htmlContent);
                    }
                    Console.WriteLine($"Switched to Overlay mode - Transparent HTML over NES");
                    break;
                    
                case ViewMode.Web:
                    // Only webview visible, emulator hidden, below menu bar
                    displayPanel.Visible = false;
                    webView.Visible = true;
                    webView.Location = new Point(0, menuHeight);
                    webView.Size = new Size(this.ClientSize.Width, availableHeight);
                    
                    // Reset viewport alignment (won't be visible anyway)
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.5f;
                    }
                    
                    // Load Google for testing
                    if (isWebViewInitialized && webView.CoreWebView2 != null)
                    {
                        webView.CoreWebView2.Navigate("https://www.google.com");
                    }
                    Console.WriteLine("Switched to Web mode");
                    break;
            }
            
            this.ResumeLayout();
            this.PerformLayout();
            this.Refresh();
        }
        
        public MainForm()
        {
            try
            {
                Console.WriteLine("MainForm constructor starting...");
                InitializeComponent();
                Console.WriteLine("InitializeComponent completed");
                
                // Load config first so settings are available for emulator initialization
                LoadConfig();
                Console.WriteLine("LoadConfig completed");
            
            // Apply profiling configuration
            PerformanceProfiler.Enabled = config.ProfilingEnabled;
            
                // Setup key mappings after config is loaded
                SetupKeyMapping();
                Console.WriteLine("SetupKeyMapping completed");
                
                // Finally initialize and start emulator
                InitializeEmulator();
                Console.WriteLine("InitializeEmulator completed");
                
                Console.WriteLine("MainForm constructor completed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MainForm constructor exception: {ex}");
                MessageBox.Show($"Error initializing MainForm:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                    "Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw;
            }
        }

        internal Corruptor Corruptor => corruptor;
        internal ImagineEngine? ImagineEngineInstance => imagineEngine;
        internal bool IsEmulatorReady => nes != null;
        internal NES? CurrentNes => nes;

        private void NotifyCorruptorChanged()
        {
            try { CorruptorStateChanged?.Invoke(); }
            catch (Exception ex) { Console.WriteLine($"CorruptorStateChanged error: {ex.Message}"); }
        }

        internal void RaiseCorruptorChangedPublic() => NotifyCorruptorChanged();

        internal CorruptorSnapshot? GetCorruptorSnapshot()
        {
            // Use TryEnter with timeout to avoid blocking the UI thread
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(corruptorLock, 100, ref lockTaken);
                if (!lockTaken)
                {
                    // Couldn't get lock quickly, return null to avoid blocking
                    return null;
                }
                
                return new CorruptorSnapshot
                {
                    CorruptIntensity = corruptor.CorruptIntensity,
                    BlastType = corruptor.BlastType,
                    MemoryDomains = corruptor.MemoryDomains.ToList(),
                    AutoCorrupt = corruptor.AutoCorrupt,
                    LastBlastInfo = corruptor.LastBlastInfo,
                    StubbornMode = corruptor.StubbornMode,
                    CrashBehavior = corruptor.CrashBehavior,
                    GhBaseStates = corruptor.GhBaseStates.ToList(),
                    GhStash = corruptor.GhStash.ToList(),
                    GhStockpile = corruptor.GhStockpile.ToList(),
                    GhSelectedBaseId = corruptor.GhSelectedBaseId,
                    GhLoadOnOperation = corruptor.GhLoadOnOperation
                };
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(corruptorLock);
            }
        }

        /// <summary>
        /// Gets a snapshot of the current emulator framebuffer.
        /// </summary>
        /// <returns>A Bitmap containing the current frame, or null if not available.</returns>
        public Bitmap? GetScreenshot()
        {
            if (frameBuffer == null) return null;
            return frameBuffer.ToBitmap();
        }

        private void QueueEmuAction(Action action)
        {
            emulationActions.Enqueue(action);
        }

        internal Task<T> RunOnEmulationThreadAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            QueueEmuAction(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        internal Task RunOnEmulationThreadAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            QueueEmuAction(() =>
            {
                try { action(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        internal void WithNes(Action<NES> action)
        {
            QueueEmuAction(() =>
            {
                var n = nes;
                if (n == null) return;
                try { action(n); }
                catch (Exception ex) { Console.WriteLine($"WithNes action error: {ex.Message}"); }
            });
        }
        
        private void InitializeComponent()
        {
            this.Text = "BrokenNes - Windows";
            this.ClientSize = new Size(1280, 720);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.KeyPreview = true;
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            this.AllowDrop = true;
            this.DragEnter += MainForm_DragEnter;
            this.DragDrop += MainForm_DragDrop;
            this.Resize += MainForm_Resize;
            
            // Create menu bar
            var menuStrip = new MenuStrip();
            
            // Emulator menu (merged File and Emulation)
            var emulatorMenu = new ToolStripMenuItem("&Emulator");
            
            var loadRomItem = new ToolStripMenuItem("&Load Rom...", null, LoadRom_Click);
            loadRomItem.ShortcutKeys = Keys.Control | Keys.O;
            emulatorMenu.DropDownItems.Add(loadRomItem);
            
            var closeRomItem = new ToolStripMenuItem("&Close Rom", null, CloseRom_Click);
            emulatorMenu.DropDownItems.Add(closeRomItem);
            
            emulatorMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var pauseResumeItem = new ToolStripMenuItem("&Pause/Resume", null, PauseResume_Click);
            emulatorMenu.DropDownItems.Add(pauseResumeItem);
            
            var resetItem = new ToolStripMenuItem("&Reset Emulator", null, ResetEmulator_Click);
            resetItem.ShortcutKeys = Keys.Control | Keys.R;
            emulatorMenu.DropDownItems.Add(resetItem);
            
            emulatorMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var loadStateItem = new ToolStripMenuItem("Load State...", null, LoadStateFromFile_Click);
            loadStateItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.L;
            emulatorMenu.DropDownItems.Add(loadStateItem);
            
            var saveStateItem = new ToolStripMenuItem("Save State...", null, SaveStateToFile_Click);
            saveStateItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.S;
            emulatorMenu.DropDownItems.Add(saveStateItem);

            var screenshotItem = new ToolStripMenuItem("Take Screenshot", null, TakeScreenshot_Click);
            screenshotItem.ShortcutKeys = Keys.F12;
            emulatorMenu.DropDownItems.Add(screenshotItem);

            var openFolderItem = new ToolStripMenuItem("Open Emulator Folder", null, OpenEmulatorFolder_Click);
            emulatorMenu.DropDownItems.Add(openFolderItem);
            
            emulatorMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var quickLoadItem = new ToolStripMenuItem("Quick Load State", null, QuickLoadState_Click);
            quickLoadItem.ShortcutKeys = Keys.F5;
            emulatorMenu.DropDownItems.Add(quickLoadItem);
            
            var quickSaveItem = new ToolStripMenuItem("Quick Save State", null, QuickSaveState_Click);
            quickSaveItem.ShortcutKeys = Keys.F7;
            emulatorMenu.DropDownItems.Add(quickSaveItem);
            
            emulatorMenu.DropDownItems.Add(new ToolStripSeparator());
            
            recentRomsMenu = new ToolStripMenuItem("Recent &Roms");
            emulatorMenu.DropDownItems.Add(recentRomsMenu);
            // Recent ROMs menu will be populated after LoadConfig() is called
            
            emulatorMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var exitItem = new ToolStripMenuItem("E&xit", null, (s, e) => Application.Exit());
            exitItem.ShortcutKeys = Keys.Alt | Keys.F4;
            emulatorMenu.DropDownItems.Add(exitItem);
            
            menuStrip.Items.Add(emulatorMenu);
            
            // Config menu
            var configMenu = new ToolStripMenuItem("&Config");
            
            // Display submenu
            var displayMenu = new ToolStripMenuItem("&Display");
            
            var pixelPerfectItem = new ToolStripMenuItem("Force Pixel Perfect", null, TogglePixelPerfect_Click);
            pixelPerfectItem.CheckOnClick = true;
            displayMenu.DropDownItems.Add(pixelPerfectItem);
            
            var nativeAspectItem = new ToolStripMenuItem("Force Native Aspect Ratio", null, ToggleNativeAspect_Click);
            nativeAspectItem.CheckOnClick = true;
            displayMenu.DropDownItems.Add(nativeAspectItem);
            
            var nearestNeighborItem = new ToolStripMenuItem("Scaling Nearest Neighbor", null, ToggleNearestNeighbor_Click);
            nearestNeighborItem.CheckOnClick = true;
            displayMenu.DropDownItems.Add(nearestNeighborItem);
            
            displayMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var zoom1xItem = new ToolStripMenuItem("Resize Zoom 1x", null, (s, e) => SetWindowZoom(1));
            displayMenu.DropDownItems.Add(zoom1xItem);
            
            var zoom2xItem = new ToolStripMenuItem("Resize Zoom 2x", null, (s, e) => SetWindowZoom(2));
            displayMenu.DropDownItems.Add(zoom2xItem);
            
            var zoom4xItem = new ToolStripMenuItem("Resize Zoom 4x", null, (s, e) => SetWindowZoom(4));
            displayMenu.DropDownItems.Add(zoom4xItem);
            
            displayMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var fullScreenItem = new ToolStripMenuItem("Full Screen", null, (s, e) => ToggleFullscreen());
            fullScreenItem.ShortcutKeys = Keys.Alt | Keys.Enter;
            displayMenu.DropDownItems.Add(fullScreenItem);
            
            var hideMenuBarItem = new ToolStripMenuItem("Hide Menu Bar in Full Screen", null, ToggleHideMenuBar_Click);
            hideMenuBarItem.CheckOnClick = true;
            hideMenuBarItem.Checked = config.HideMenuBarInFullscreen;
            displayMenu.DropDownItems.Add(hideMenuBarItem);
            
            configMenu.DropDownItems.Add(displayMenu);
            
            // Sound submenu
            var soundMenu = new ToolStripMenuItem("&Sound");
            
            // Sound channels submenu
            var channelsMenu = new ToolStripMenuItem("Sound Channels");
            
            var square1Item = new ToolStripMenuItem("Square 1", null, (s, e) => ToggleSoundChannel(0));
            square1Item.CheckOnClick = true;
            channelsMenu.DropDownItems.Add(square1Item);
            
            var square2Item = new ToolStripMenuItem("Square 2", null, (s, e) => ToggleSoundChannel(1));
            square2Item.CheckOnClick = true;
            channelsMenu.DropDownItems.Add(square2Item);
            
            var triangleItem = new ToolStripMenuItem("Triangle", null, (s, e) => ToggleSoundChannel(2));
            triangleItem.CheckOnClick = true;
            channelsMenu.DropDownItems.Add(triangleItem);
            
            var noiseItem = new ToolStripMenuItem("Noise", null, (s, e) => ToggleSoundChannel(3));
            noiseItem.CheckOnClick = true;
            channelsMenu.DropDownItems.Add(noiseItem);
            
            var dmcItem = new ToolStripMenuItem("DMC", null, (s, e) => ToggleSoundChannel(4));
            dmcItem.CheckOnClick = true;
            channelsMenu.DropDownItems.Add(dmcItem);
            
            soundMenu.DropDownItems.Add(channelsMenu);
            
            // Sound quality submenu
            var qualityMenu = new ToolStripMenuItem("Sound Quality");
            
            var quality22050Item = new ToolStripMenuItem("22050 Hz (Low)", null, (s, e) => SetSoundQuality(22050));
            qualityMenu.DropDownItems.Add(quality22050Item);
            
            var quality44100Item = new ToolStripMenuItem("44100 Hz (Standard)", null, (s, e) => SetSoundQuality(44100));
            qualityMenu.DropDownItems.Add(quality44100Item);
            
            var quality48000Item = new ToolStripMenuItem("48000 Hz (High)", null, (s, e) => SetSoundQuality(48000));
            qualityMenu.DropDownItems.Add(quality48000Item);
            
            soundMenu.DropDownItems.Add(qualityMenu);
            
            // Sound buffer submenu
            var bufferMenu = new ToolStripMenuItem("Sound Buffer");
            
            var buffer512Item = new ToolStripMenuItem("512 samples (Lowest Latency)", null, (s, e) => SetSoundBuffer(512));
            bufferMenu.DropDownItems.Add(buffer512Item);
            
            var buffer1024Item = new ToolStripMenuItem("1024 samples (Low Latency)", null, (s, e) => SetSoundBuffer(1024));
            bufferMenu.DropDownItems.Add(buffer1024Item);
            
            var buffer2048Item = new ToolStripMenuItem("2048 samples (Normal)", null, (s, e) => SetSoundBuffer(2048));
            bufferMenu.DropDownItems.Add(buffer2048Item);
            
            var buffer4096Item = new ToolStripMenuItem("4096 samples (Stable)", null, (s, e) => SetSoundBuffer(4096));
            bufferMenu.DropDownItems.Add(buffer4096Item);
            
            soundMenu.DropDownItems.Add(bufferMenu);
            
            configMenu.DropDownItems.Add(soundMenu);
            
            // Controllers submenu
            var controllersMenu = new ToolStripMenuItem("&Controllers");
            
            var player1Menu = new ToolStripMenuItem("Player 1 Configuration...", null, (s, e) => OpenControllerConfig(1));
            controllersMenu.DropDownItems.Add(player1Menu);
            
            var player2Menu = new ToolStripMenuItem("Player 2 Configuration...", null, (s, e) => OpenControllerConfig(2));
            controllersMenu.DropDownItems.Add(player2Menu);
            
            configMenu.DropDownItems.Add(controllersMenu);
            
            // Background submenu - automatically populated via reflection
            var backgroundMenu = new ToolStripMenuItem("&Backgrounds");
            
            // Get all available backgrounds via reflection
            var availableBackgrounds = BrokenNes.Windows.Rendering.NesDirectXRenderer.GetAvailableBackgrounds();
            foreach (var backgroundName in availableBackgrounds)
            {
                if (backgroundName == "---")
                {
                    // Add separator
                    backgroundMenu.DropDownItems.Add(new ToolStripSeparator());
                }
                else
                {
                    var menuItem = new ToolStripMenuItem(backgroundName, null, (s, e) => SetBackground(backgroundName));
                    backgroundMenu.DropDownItems.Add(menuItem);
                }
            }
            
            configMenu.DropDownItems.Add(backgroundMenu);
            
            // Null Provider submenu - automatically populated via reflection
            var nullProviderMenu = new ToolStripMenuItem("&Null Providers (Test ROM)");
            
            // Get all available null providers via reflection
            var availableNullProviders = NesEmulator.NES.GetAvailableNullProviders();
            foreach (var providerName in availableNullProviders)
            {
                var menuItem = new ToolStripMenuItem(providerName, null, (s, e) => SetNullProvider(providerName));
                nullProviderMenu.DropDownItems.Add(menuItem);
            }
            
            configMenu.DropDownItems.Add(nullProviderMenu);
            
            // Visual effects for backgrounds
            var backgroundEffectsMenu = new ToolStripMenuItem("Background &Effects");
            
            var scanlinesItem = new ToolStripMenuItem("Render Scanlines", null, ToggleScanlines_Click);
            scanlinesItem.CheckOnClick = true;
            backgroundEffectsMenu.DropDownItems.Add(scanlinesItem);
            
            var shadowItem = new ToolStripMenuItem("Render Viewport Shadow", null, ToggleViewportShadow_Click);
            shadowItem.CheckOnClick = true;
            backgroundEffectsMenu.DropDownItems.Add(shadowItem);
            
            configMenu.DropDownItems.Add(backgroundEffectsMenu);
            
            // Crash Behavior submenu
            var crashBehaviorMenu = new ToolStripMenuItem("C&rash Behavior");
            
            var redScreenItem = new ToolStripMenuItem("Red Screen", null, (s, e) => SetCrashBehavior("RedScreen"));
            crashBehaviorMenu.DropDownItems.Add(redScreenItem);
            
            var ignoreErrorsItem = new ToolStripMenuItem("Ignore Errors", null, (s, e) => SetCrashBehavior("IgnoreErrors"));
            crashBehaviorMenu.DropDownItems.Add(ignoreErrorsItem);
            
            var imagineFixItem = new ToolStripMenuItem("Imagine Fix (Not Implemented)", null, (s, e) => { /* Disabled */ });
            imagineFixItem.Enabled = false;
            crashBehaviorMenu.DropDownItems.Add(imagineFixItem);
            
            configMenu.DropDownItems.Add(crashBehaviorMenu);
            
            configMenu.DropDownItems.Add(new ToolStripSeparator());
            
            // Emulation options
            var noSpeedLimitItem = new ToolStripMenuItem("No Speed Limit", null, ToggleNoSpeedLimit_Click);
            noSpeedLimitItem.CheckOnClick = true;
            configMenu.DropDownItems.Add(noSpeedLimitItem);
            
            var speedControlItem = new ToolStripMenuItem("Speed Control...", null, OpenSpeedControl_Click);
            configMenu.DropDownItems.Add(speedControlItem);
            
            var showFpsItem = new ToolStripMenuItem("Show FPS and Input", null, ToggleShowFps_Click);
            showFpsItem.CheckOnClick = true;
            configMenu.DropDownItems.Add(showFpsItem);
            
            // VSync option (DirectX only)
            if (useDirectX)
            {
                var vsyncItem = new ToolStripMenuItem("V-Sync", null, ToggleVSync_Click);
                vsyncItem.CheckOnClick = true;
                configMenu.DropDownItems.Add(vsyncItem);
            }
            
            var startProfilingItem = new ToolStripMenuItem("Start Profiling Performance", null, ToggleProfiling_Click);
            startProfilingItem.CheckOnClick = true;
            configMenu.DropDownItems.Add(startProfilingItem);
            
            configMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var autoScrambleItem = new ToolStripMenuItem("Auto-Scramble Cores (Testing)", null, ToggleAutoScrambleCores_Click);
            autoScrambleItem.CheckOnClick = true;
            configMenu.DropDownItems.Add(autoScrambleItem);
            
            configMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var showConsoleItem = new ToolStripMenuItem("Show Console", null, ToggleShowConsole_Click);
            showConsoleItem.CheckOnClick = true;
            configMenu.DropDownItems.Add(showConsoleItem);
            
            menuStrip.Items.Add(configMenu);

            // Tools menu
            var toolsMenu = new ToolStripMenuItem("&Tools");
            var rtcItem = new ToolStripMenuItem("Real-Time Corruptor", null, OpenRtcTool_Click);
            var ghItem = new ToolStripMenuItem("Glitch Harvester", null, OpenGhTool_Click);
            var imagineItem = new ToolStripMenuItem("Imagine", null, OpenImagineTool_Click);
            var hexEditorItem = new ToolStripMenuItem("Hex Editor", null, OpenHexEditor_Click);
            toolsMenu.DropDownItems.Add(rtcItem);
            toolsMenu.DropDownItems.Add(ghItem);
            toolsMenu.DropDownItems.Add(imagineItem);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(hexEditorItem);
            menuStrip.Items.Add(toolsMenu);
            
            // Web menu for view modes
            var webMenu = new ToolStripMenuItem("&Web");
            
            var emulatorModeItem = new ToolStripMenuItem("Emulator Mode", null, (s, e) => SwitchViewMode(ViewMode.Emulator));
            emulatorModeItem.ShortcutKeys = Keys.Control | Keys.D1;
            webMenu.DropDownItems.Add(emulatorModeItem);
            
            var widgetModeItem = new ToolStripMenuItem("Widget Mode", null, (s, e) => SwitchViewMode(ViewMode.Widget));
            widgetModeItem.ShortcutKeys = Keys.Control | Keys.D2;
            webMenu.DropDownItems.Add(widgetModeItem);
            
            var webModeItem = new ToolStripMenuItem("Web Mode", null, (s, e) => SwitchViewMode(ViewMode.Web));
            webModeItem.ShortcutKeys = Keys.Control | Keys.D3;
            webMenu.DropDownItems.Add(webModeItem);
            
            var overlayModeItem = new ToolStripMenuItem("Overlay Mode", null, (s, e) => SwitchViewMode(ViewMode.Overlay));
            overlayModeItem.ShortcutKeys = Keys.Control | Keys.D4;
            webMenu.DropDownItems.Add(overlayModeItem);
            
            menuStrip.Items.Add(webMenu);
            
            // Core selection menus: SHADER, APU, CPU, PPU
            shaderMenu = new ToolStripMenuItem("&SHADER");
            menuStrip.Items.Add(shaderMenu);

            apuMenu = new ToolStripMenuItem("&APU");
            menuStrip.Items.Add(apuMenu);

            cpuMenu = new ToolStripMenuItem("&CPU");
            menuStrip.Items.Add(cpuMenu);

            ppuMenu = new ToolStripMenuItem("&PPU");
            menuStrip.Items.Add(ppuMenu);
            
            // Help menu
            var helpMenu = new ToolStripMenuItem("&Help");
            
            var aboutItem = new ToolStripMenuItem("&About", null, About_Click);
            helpMenu.DropDownItems.Add(aboutItem);
            
            menuStrip.Items.Add(helpMenu);
            
            this.MainMenuStrip = menuStrip;
            this.Controls.Add(menuStrip);
            
            // Create display panel - positioned below menu bar
            displayPanel = new Panel
            {
                BackColor = Color.Black,
                Location = new Point(0, menuStrip.Height),
                Size = new Size(this.ClientSize.Width, this.ClientSize.Height - menuStrip.Height)
            };
            
            // Add double-click handler for fullscreen toggle (fallback if DirectX not used)
            displayPanel.DoubleClick += (s, e) => ToggleFullscreen();
            
            this.Controls.Add(displayPanel);
            
            // Initialize WebView2
            try
            {
                webView = new WebView2
                {
                    Visible = false // Start hidden
                };
                this.Controls.Add(webView);
                
                // Initialize WebView2 asynchronously
                InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize WebView2: {ex.Message}", 
                    "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            
            // Create DirectX renderer for hardware-accelerated rendering
            try
            {
                dxRenderer = new NesDirectXRenderer
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black
                };
                
                // Add double-click handler for fullscreen toggle
                dxRenderer.DoubleClick += (s, e) => ToggleFullscreen();
                
                displayPanel.Controls.Add(dxRenderer);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize DirectX renderer: {ex.Message}\nFalling back to software rendering.", 
                    "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                useDirectX = false;
            }
            
            // Handle keyboard input
            this.KeyDown += MainForm_KeyDown;
            this.KeyUp += MainForm_KeyUp;
        }
        
        private void InitializeEmulator()
        {
            // Initialize the framebuffers (double buffering)
            frameBuffer = new DirectBitmap(NES_WIDTH, NES_HEIGHT);
            backBuffer = new DirectBitmap(NES_WIDTH, NES_HEIGHT);
            
            // Initialize DirectX renderer if available
            if (useDirectX && dxRenderer != null)
            {
                // Apply VSync setting from config
                dxRenderer.EnableVSync = config.EnableVSync;
                
                try
                {
                    dxRenderer.Initialize(NES_WIDTH, NES_HEIGHT);
                    NesShaderControl.Initialize(dxRenderer);
                    
                    // Apply background setting from config
                    dxRenderer.SetBackground(config.SelectedBackground);
                    
                    // Apply background effects from config
                    dxRenderer.RenderScanlines = config.RenderScanlines;
                    dxRenderer.RenderViewportShadow = config.RenderViewportShadow;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"DirectX initialization failed: {ex.Message}\nFalling back to software rendering.",
                        "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    useDirectX = false;
                }
            }
            
            // Initialize audio manager
            try
            {
                audioManager = new AudioManager(sampleRate: config.SoundQuality, channels: 1);
                Console.WriteLine("AudioManager initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize audio: {ex.Message}");
                MessageBox.Show($"Audio initialization failed: {ex.Message}\n\nThe emulator will run without sound.",
                    "Audio Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            
            // Initialize auto-scramble timer
            autoScrambleTimer = new System.Windows.Forms.Timer();
            autoScrambleTimer.Interval = 200; // 200ms (5 times per second)
            autoScrambleTimer.Tick += AutoScrambleTimer_Tick;
            
            // Start auto-scramble if enabled in config
            if (config.AutoScrambleCores)
            {
                autoScrambleTimer.Start();
            }
            
            // Load the default embedded ROM
            LoadEmbeddedRom();
        }
        
        private void SetupKeyMapping()
        {
            keyMap.Clear();
            
            Console.WriteLine("Setting up key mappings from config:");
            
            // Get or create Player 1 controller config
            var p1Config = config.GetPlayerController(1);
            
            // Use new config system
            if (inputManager == null)
            {
                inputManager = new InputManager(SharpDX.XInput.UserIndex.One);
            }
            inputManager.SetPlayerConfig(p1Config);
            
            Console.WriteLine($"Player 1 controller configured:");
            Console.WriteLine($"  A: {p1Config.A.DisplayName}");
            Console.WriteLine($"  B: {p1Config.B.DisplayName}");
            Console.WriteLine($"  Select: {p1Config.Select.DisplayName}");
            Console.WriteLine($"  Start: {p1Config.Start.DisplayName}");
            Console.WriteLine($"  Up: {p1Config.Up.DisplayName}");
            Console.WriteLine($"  Down: {p1Config.Down.DisplayName}");
            Console.WriteLine($"  Left: {p1Config.Left.DisplayName}");
            Console.WriteLine($"  Right: {p1Config.Right.DisplayName}");
            
            // Get or create Player 2 controller config
            var p2Config = config.GetPlayerController(2);
            
            if (inputManager2 == null)
            {
                inputManager2 = new InputManager(SharpDX.XInput.UserIndex.Two);
            }
            inputManager2.SetPlayerConfig(p2Config);
            
            Console.WriteLine($"Player 2 controller configured:");
            Console.WriteLine($"  A: {p2Config.A.DisplayName}");
            Console.WriteLine($"  B: {p2Config.B.DisplayName}");
            Console.WriteLine($"  Select: {p2Config.Select.DisplayName}");
            Console.WriteLine($"  Start: {p2Config.Start.DisplayName}");
            Console.WriteLine($"  Up: {p2Config.Up.DisplayName}");
            Console.WriteLine($"  Down: {p2Config.Down.DisplayName}");
            Console.WriteLine($"  Left: {p2Config.Left.DisplayName}");
            Console.WriteLine($"  Right: {p2Config.Right.DisplayName}");
        }
        
        private void LoadConfig()
        {
            try
            {
                config = EmulatorConfig.Load();
                useDirectX = config.UseDirectX;
                
                // Update recent ROMs menu now that config is loaded
                if (recentRomsMenu != null)
                {
                    UpdateRecentRomsMenu(recentRomsMenu);
                }
                
                // Update Config menu checkmarks
                UpdateConfigMenus();
                
                // Apply image settings
                ApplyImageSettings();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
                config = new EmulatorConfig();
            }
        }
        
        private void UpdateRecentRomsMenu(ToolStripMenuItem recentMenu)
        {
            recentMenu.DropDownItems.Clear();
            
            if (config.RecentRoms.Count == 0)
            {
                var emptyItem = new ToolStripMenuItem("(No recent ROMs)");
                emptyItem.Enabled = false;
                recentMenu.DropDownItems.Add(emptyItem);
            }
            else
            {
                foreach (var romPath in config.RecentRoms)
                {
                    var fileName = Path.GetFileName(romPath);
                    var item = new ToolStripMenuItem(fileName, null, (s, e) => LoadRomFile(romPath));
                    item.ToolTipText = romPath;
                    recentMenu.DropDownItems.Add(item);
                }
                
                recentMenu.DropDownItems.Add(new ToolStripSeparator());
                
                var clearItem = new ToolStripMenuItem("Clear Recent", null, (s, e) => 
                {
                    config.ClearRecentRoms();
                    UpdateRecentRomsMenu(recentMenu);
                });
                recentMenu.DropDownItems.Add(clearItem);
            }
        }
        
        private void UpdateConfigMenus()
        {
            // Find the Config menu
            var configMenu = this.MainMenuStrip?.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "&Config");
            
            if (configMenu == null) return;
            
            // Update Display submenu checkmarks
            var displayMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "&Display");
            
            if (displayMenu != null)
            {
                foreach (var item in displayMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (item.Text.Contains("Pixel Perfect"))
                        item.Checked = config.ForcePixelPerfect;
                    else if (item.Text.Contains("Native Aspect"))
                        item.Checked = config.ForceNativeAspectRatio;
                    else if (item.Text.Contains("Nearest Neighbor"))
                        item.Checked = config.ScalingNearestNeighbor;
                    else if (item.Text.Contains("Hide Menu Bar"))
                        item.Checked = config.HideMenuBarInFullscreen;
                    // Zoom options are not checkboxes, so we don't update them
                }
            }
            
            // Update emulation options checkmarks
            foreach (var item in configMenu.DropDownItems.OfType<ToolStripMenuItem>())
            {
                if (item.Text.Contains("No Speed Limit"))
                    item.Checked = config.NoSpeedLimit;
                else if (item.Text.Contains("Show FPS and Input"))
                    item.Checked = config.ShowFps;
                else if (item.Text.Contains("V-Sync"))
                    item.Checked = config.EnableVSync;
                else if (item.Text.Contains("Start Profiling Performance"))
                    item.Checked = config.ProfilingEnabled;
                else if (item.Text.Contains("Auto-Scramble Cores"))
                    item.Checked = config.AutoScrambleCores;
                else if (item.Text.Contains("Show Console"))
                    item.Checked = config.ShowConsole;
            }
            
            // Update Crash Behavior submenu checkmarks
            var crashBehaviorMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "C&rash Behavior");
            
            if (crashBehaviorMenu != null)
            {
                foreach (var item in crashBehaviorMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (item.Text.Contains("Red Screen"))
                        item.Checked = (config.CrashBehavior == "RedScreen");
                    else if (item.Text.Contains("Ignore Errors"))
                        item.Checked = (config.CrashBehavior == "IgnoreErrors");
                    else if (item.Text.Contains("Imagine Fix"))
                        item.Checked = (config.CrashBehavior == "ImagineFix");
                }
            }
            
            // Update Sound submenu checkmarks
            var soundMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "&Sound");
            
            if (soundMenu != null)
            {
                // Update Sound Channels submenu
                var channelsMenu = soundMenu.DropDownItems.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(m => m.Text == "Sound Channels");
                
                if (channelsMenu != null)
                {
                    var channelItems = channelsMenu.DropDownItems.OfType<ToolStripMenuItem>().ToArray();
                    for (int i = 0; i < channelItems.Length && i < 5; i++)
                    {
                        channelItems[i].Checked = (config.EnabledChannels & (1 << i)) != 0;
                    }
                }
                
                // Update Sound Quality submenu
                var qualityMenu = soundMenu.DropDownItems.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(m => m.Text == "Sound Quality");
                
                if (qualityMenu != null)
                {
                    foreach (var item in qualityMenu.DropDownItems.OfType<ToolStripMenuItem>())
                    {
                        if (item.Text.Contains("22050"))
                            item.Checked = (config.SoundQuality == 22050);
                        else if (item.Text.Contains("44100"))
                            item.Checked = (config.SoundQuality == 44100);
                        else if (item.Text.Contains("48000"))
                            item.Checked = (config.SoundQuality == 48000);
                    }
                }
                
                // Update Sound Buffer submenu
                var bufferMenu = soundMenu.DropDownItems.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(m => m.Text == "Sound Buffer");
                
                if (bufferMenu != null)
                {
                    foreach (var item in bufferMenu.DropDownItems.OfType<ToolStripMenuItem>())
                    {
                        if (item.Text.Contains("512"))
                            item.Checked = (config.SoundBuffer == 512);
                        else if (item.Text.Contains("1024"))
                            item.Checked = (config.SoundBuffer == 1024);
                        else if (item.Text.Contains("2048"))
                            item.Checked = (config.SoundBuffer == 2048);
                        else if (item.Text.Contains("4096"))
                            item.Checked = (config.SoundBuffer == 4096);
                    }
                }
            }
            
            // Update Backgrounds submenu checkmarks
            var backgroundMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "&Backgrounds");
            
            if (backgroundMenu != null)
            {
                foreach (var item in backgroundMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    // Check if this item matches the currently selected background
                    item.Checked = item.Text.Equals(config.SelectedBackground, StringComparison.OrdinalIgnoreCase);
                }
            }
            
            // Update Null Providers submenu checkmarks
            var nullProviderMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "&Null Providers (Test ROM)");
            
            if (nullProviderMenu != null)
            {
                foreach (var item in nullProviderMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    // Check if this item matches the currently selected null provider
                    item.Checked = item.Text.Equals(config.SelectedNullProvider, StringComparison.OrdinalIgnoreCase);
                }
            }
            
            // Update Background Effects submenu checkmarks
            var backgroundEffectsMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "Background &Effects");
            
            if (backgroundEffectsMenu != null)
            {
                foreach (var item in backgroundEffectsMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (item.Text.Contains("Render Scanlines"))
                        item.Checked = config.RenderScanlines;
                    else if (item.Text.Contains("Render Viewport Shadow"))
                        item.Checked = config.RenderViewportShadow;
                }
            }
        }
        
        private void UpdateCoresMenus()
        {
            if (nes == null) return;
            
            // SHADER
            shaderMenu.DropDownItems.Clear();
            
            // Add shader enable/disable toggle
            var toggleShaderItem = new ToolStripMenuItem("Enable Shaders", null, (s, e) => {
                if (useDirectX && dxRenderer != null)
                {
                    dxRenderer.UseShader = !dxRenderer.UseShader;
                    ((ToolStripMenuItem)s).Checked = dxRenderer.UseShader;
                    config.ShadersEnabled = dxRenderer.UseShader;
                    config.Save();
                }
            });
            toggleShaderItem.Checked = useDirectX && dxRenderer?.UseShader == true;
            shaderMenu.DropDownItems.Add(toggleShaderItem);
            shaderMenu.DropDownItems.Add(new ToolStripSeparator());
            
            // Add DirectX shader options
            if (useDirectX && dxRenderer != null)
            {
                foreach (var shaderName in NesDirectXRenderer.GetAvailableShaders())
                {
                    var shaderInfo = NesShaderControl.GetShaderInfo(
                        Enum.Parse<NesShaderManager.ShaderType>(shaderName));
                    var item = new ToolStripMenuItem(shaderInfo.DisplayName, null, (s, e) => {
                        NesShaderControl.SwitchShader(shaderName);
                        config.CurrentShader = shaderName;
                        config.Save();
                        UpdateCoresMenus(); // Refresh to update checkmarks
                    });
                    item.ToolTipText = shaderInfo.Description;
                    item.Checked = (shaderName == config.CurrentShader);
                    shaderMenu.DropDownItems.Add(item);
                }
                
                shaderMenu.DropDownItems.Add(new ToolStripSeparator());
                
                // Shader strength control
                var strengthMenu = new ToolStripMenuItem("Shader Strength");
                foreach (var strength in new[] { 0.5f, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f })
                {
                    var strengthItem = new ToolStripMenuItem($"{strength:F1}x", null, (s, e) => {
                        NesShaderControl.SetShaderStrength(strength);
                        config.ShaderStrength = strength;
                        config.Save();
                        UpdateCoresMenus(); // Refresh to update checkmarks
                    });
                    strengthItem.Checked = (Math.Abs(config.ShaderStrength - strength) < 0.01f);
                    strengthMenu.DropDownItems.Add(strengthItem);
                }
                shaderMenu.DropDownItems.Add(strengthMenu);
            }
            else
            {
                var noShaderItem = new ToolStripMenuItem("DirectX not available");
                noShaderItem.Enabled = false;
                shaderMenu.DropDownItems.Add(noShaderItem);
            }

            // APU - single select with checkmarks
            apuMenu.DropDownItems.Clear();
            string currentApuCore = config.SelectedApuCore;
            foreach (var coreId in CoreRegistry.ApuIds)
            {
                var item = new ToolStripMenuItem(coreId, null, (s, e) => SetApuCore(coreId));
                item.Checked = (coreId == currentApuCore);
                apuMenu.DropDownItems.Add(item);
            }

            // CPU - single select with checkmarks
            cpuMenu.DropDownItems.Clear();
            string currentCpuCore = config.SelectedCpuCore;
            foreach (var coreId in CoreRegistry.CpuIds)
            {
                var item = new ToolStripMenuItem(coreId, null, (s, e) => SetCpuCore(coreId));
                item.Checked = (coreId == currentCpuCore);
                cpuMenu.DropDownItems.Add(item);
            }

            // PPU - single select with checkmarks
            ppuMenu.DropDownItems.Clear();
            string currentPpuCore = config.SelectedPpuCore;
            foreach (var coreId in CoreRegistry.PpuIds)
            {
                var item = new ToolStripMenuItem(coreId, null, (s, e) => SetPpuCore(coreId));
                item.Checked = (coreId == currentPpuCore);
                ppuMenu.DropDownItems.Add(item);
            }
        }
        
        private void ApplySavedCoreSelections()
        {
            if (nes == null) return;
            
            // Apply CPU core (default to FMC if not valid)
            if (!string.IsNullOrEmpty(config.SelectedCpuCore) && CoreRegistry.CpuIds.Contains(config.SelectedCpuCore))
            {
                nes.SetCpuCore(config.SelectedCpuCore);
            }
            else
            {
                config.SelectedCpuCore = "FMC";
                nes.SetCpuCore("FMC");
                config.Save();
            }
            
            // Apply PPU core (default to FMC if not valid)
            if (!string.IsNullOrEmpty(config.SelectedPpuCore) && CoreRegistry.PpuIds.Contains(config.SelectedPpuCore))
            {
                nes.SetPpuCore(config.SelectedPpuCore);
            }
            else
            {
                config.SelectedPpuCore = "FMC";
                nes.SetPpuCore("FMC");
                config.Save();
            }
            
            // Apply APU core (default to FMC if not valid)
            if (!string.IsNullOrEmpty(config.SelectedApuCore) && CoreRegistry.ApuIds.Contains(config.SelectedApuCore))
            {
                nes.SetApuCore(config.SelectedApuCore);
            }
            else
            {
                config.SelectedApuCore = "FMC";
                nes.SetApuCore("FMC");
                config.Save();
            }
            
            // Apply shader settings
            if (useDirectX && dxRenderer != null)
            {
                // Restore shader enabled state
                dxRenderer.UseShader = config.ShadersEnabled;
                
                // Restore selected shader if valid
                if (!string.IsNullOrEmpty(config.CurrentShader))
                {
                    var availableShaders = NesDirectXRenderer.GetAvailableShaders();
                    if (availableShaders.Contains(config.CurrentShader))
                    {
                        NesShaderControl.SwitchShader(config.CurrentShader);
                    }
                }
                
                // Restore shader strength
                if (config.ShaderStrength > 0)
                {
                    NesShaderControl.SetShaderStrength(config.ShaderStrength);
                }
            }
        }
        
        private void SetCpuCore(string coreId)
        {
            if (nes == null) return;
            nes.SetCpuCore(coreId);
            config.SelectedCpuCore = coreId;
            config.Save();
            UpdateCoresMenus(); // Refresh to update checkmarks
        }
        
        private void SetPpuCore(string coreId)
        {
            if (nes == null) return;
            nes.SetPpuCore(coreId);
            config.SelectedPpuCore = coreId;
            config.Save();
            UpdateCoresMenus(); // Refresh to update checkmarks
        }
        
        private void SetApuCore(string coreId)
        {
            if (nes == null) return;
            nes.SetApuCore(coreId);
            config.SelectedApuCore = coreId;
            config.Save();
            UpdateCoresMenus(); // Refresh to update checkmarks
        }
        
        private void LoadEmbeddedRom()
        {
            try
            {
                // Load the embedded test.nes ROM
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                
                // Debug: List all embedded resources
                var allResources = assembly.GetManifestResourceNames();
                Console.WriteLine($"Found {allResources.Length} embedded resources:");
                foreach (var res in allResources)
                {
                    Console.WriteLine($"  - {res}");
                }
                
                // Try to find the correct resource name
                var resourceName = allResources.FirstOrDefault(r => r.EndsWith("test.nes"));
                if (resourceName == null)
                {
                    MessageBox.Show($"Failed to find test.nes in embedded resources.\nFound resources:\n{string.Join("\n", allResources)}", 
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                Console.WriteLine($"Loading resource: {resourceName}");
                
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        MessageBox.Show($"Failed to load embedded ROM resource: {resourceName}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    
                    byte[] romData = new byte[stream.Length];
                    int bytesRead = stream.Read(romData, 0, romData.Length);
                    Console.WriteLine($"Read {bytesRead} bytes from embedded ROM");
                    
                    // Create new NES instance
                    nes = new NES();
                    nes.LoadROM(romData);
                    nes.RomName = "test.nes";
                    
                    currentRomPath = "test.nes (embedded)";
                    this.Text = "BrokenNes - test.nes";
                    
                    // Apply saved core selections
                    ApplySavedCoreSelections();
                    
                    // Apply crash behavior
                    ApplyCrashBehavior();
                    
                    // Apply saved null provider
                    nes.SetNullProvider(config.SelectedNullProvider);

                    // Apply image settings (will force Pixel Perfect for Test ROM)
                    ApplyImageSettings();
                    
                    // Update cores menus
                    UpdateCoresMenus();
                    
                    // Start emulation automatically
                    StartEmulation();
                    
                    Console.WriteLine("Embedded ROM loaded successfully and emulation started");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading embedded ROM: {ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Console.WriteLine($"Error loading embedded ROM: {ex}");
            }
        }
        
        private void MainForm_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    string ext = Path.GetExtension(files[0]).ToLower();
                    if (ext == ".nes" || ext == ".png")
                    {
                        e.Effect = DragDropEffects.Copy;
                        return;
                    }
                }
            }
            e.Effect = DragDropEffects.None;
        }

        private void MainForm_DragDrop(object? sender, DragEventArgs e)
        {
             if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                 var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
                 if (files != null && files.Length > 0)
                 {
                     string path = files[0];
                     string ext = Path.GetExtension(path).ToLower();
                     
                     if (ext == ".nes")
                     {
                         LoadRomFile(path);
                     }
                     else if (ext == ".png")
                     {
                         LoadStateFile(path);
                     }
                 }
            }
        }

        private void MainForm_Resize(object? sender, EventArgs e)
        {
            // Reapply layout when window is resized to maintain proper positioning
            if (webView != null && displayPanel != null)
            {
                int menuHeight = GetEffectiveMenuHeight();
                int availableHeight = this.ClientSize.Height - menuHeight;
                
                switch (currentViewMode)
                {
                    case ViewMode.Emulator:
                        displayPanel.Location = new Point(0, menuHeight);
                        displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                        
                        if (useDirectX && dxRenderer != null)
                        {
                            dxRenderer.ViewportAlignmentX = 0.5f;
                        }
                        break;
                        
                    case ViewMode.Widget:
                        // Background renders full width, WebView panel overlays on right
                        displayPanel.Location = new Point(0, menuHeight);
                        displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                        
                        // Calculate NES width for panel positioning
                        float nesAspectRatio = (float)NES_WIDTH / NES_HEIGHT;
                        int nesWidth = (int)(availableHeight * nesAspectRatio);
                        int maxNesWidth = (int)(this.ClientSize.Width * 0.75f);
                        if (nesWidth > maxNesWidth)
                        {
                            nesWidth = maxNesWidth;
                        }
                        
                        // Align viewport flush to left side
                        if (useDirectX && dxRenderer != null)
                        {
                            dxRenderer.ViewportAlignmentX = 0.0f; // Flush left
                            
                            // Get actual viewport width from renderer
                            var viewportRect = dxRenderer.GetViewportRect();
                            int actualViewportWidth = (int)Math.Ceiling(viewportRect.Right);
                            if (actualViewportWidth > 0 && actualViewportWidth < this.ClientSize.Width)
                            {
                                nesWidth = actualViewportWidth;
                            }
                        }
                        
                        webView.Location = new Point(nesWidth, menuHeight);
                        webView.Size = new Size(this.ClientSize.Width - nesWidth, availableHeight);
                        webView.BringToFront();
                        break;
                        
                    case ViewMode.Overlay:
                        // Overlay mode - both occupy the same space
                        displayPanel.Location = new Point(0, menuHeight);
                        displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                        
                        if (useDirectX && dxRenderer != null)
                        {
                            dxRenderer.ViewportAlignmentX = 0.5f;
                        }
                        
                        webView.Location = new Point(0, menuHeight);
                        webView.Size = new Size(this.ClientSize.Width, availableHeight);
                        webView.BringToFront();
                        break;
                        
                    case ViewMode.Web:
                        webView.Location = new Point(0, menuHeight);
                        webView.Size = new Size(this.ClientSize.Width, availableHeight);
                        
                        if (useDirectX && dxRenderer != null)
                        {
                            dxRenderer.ViewportAlignmentX = 0.5f;
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Extend NES state JSON with UI settings (shader configuration)
        /// </summary>
        private string ExtendStateWithUISettings(string nesStateJson)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(nesStateJson);
                var root = doc.RootElement;
                
                // Create a dictionary with all existing properties plus UI settings
                var stateDict = new Dictionary<string, object>();
                
                // Copy all existing properties
                foreach (var property in root.EnumerateObject())
                {
                    stateDict[property.Name] = property.Value.Clone();
                }
                
                // Add UI settings
                if (useDirectX && dxRenderer != null)
                {
                    stateDict["uiShadersEnabled"] = dxRenderer.UseShader;
                    stateDict["uiCurrentShader"] = config.CurrentShader ?? string.Empty;
                    stateDict["uiShaderStrength"] = config.ShaderStrength;
                }
                else
                {
                    stateDict["uiShadersEnabled"] = false;
                    stateDict["uiCurrentShader"] = string.Empty;
                    stateDict["uiShaderStrength"] = 1.0f;
                }
                
                // Serialize back to JSON
                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
                return System.Text.Json.JsonSerializer.Serialize(stateDict, options);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to extend state with UI settings: {ex.Message}");
                return nesStateJson; // Return original if extension fails
            }
        }
        
        /// <summary>
        /// Restore UI settings (shader configuration) from extended state JSON
        /// </summary>
        private void RestoreUISettingsFromState(string stateJson)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(stateJson);
                var root = doc.RootElement;
                
                // Check if UI settings are present in the state
                bool hasShadersEnabled = root.TryGetProperty("uiShadersEnabled", out var shadersEnabledEl);
                bool hasCurrentShader = root.TryGetProperty("uiCurrentShader", out var currentShaderEl);
                bool hasShaderStrength = root.TryGetProperty("uiShaderStrength", out var shaderStrengthEl);
                
                if (!hasShadersEnabled && !hasCurrentShader && !hasShaderStrength)
                {
                    // Old savestate without UI settings - skip restoration
                    Console.WriteLine("Savestate does not contain UI settings - keeping current configuration");
                }
                else
                {
                    // Restore shader settings if DirectX is available
                    if (useDirectX && dxRenderer != null)
                    {
                        // Restore shaders enabled state
                        if (hasShadersEnabled)
                        {
                            bool shadersEnabled = shadersEnabledEl.GetBoolean();
                            dxRenderer.UseShader = shadersEnabled;
                            config.ShadersEnabled = shadersEnabled;
                            Console.WriteLine($"Restored shaders enabled: {shadersEnabled}");
                        }
                        
                        // Restore current shader
                        if (hasCurrentShader)
                        {
                            string currentShader = currentShaderEl.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(currentShader))
                            {
                                var availableShaders = NesDirectXRenderer.GetAvailableShaders();
                                if (availableShaders.Contains(currentShader))
                                {
                                    NesShaderControl.SwitchShader(currentShader);
                                    config.CurrentShader = currentShader;
                                    Console.WriteLine($"Restored shader: {currentShader}");
                                }
                            }
                        }
                        
                        // Restore shader strength
                        if (hasShaderStrength)
                        {
                            float shaderStrength = shaderStrengthEl.GetSingle();
                            if (shaderStrength > 0)
                            {
                                NesShaderControl.SetShaderStrength(shaderStrength);
                                config.ShaderStrength = shaderStrength;
                                Console.WriteLine($"Restored shader strength: {shaderStrength}");
                            }
                        }
                        
                        // Note: We don't save config here - these are temporary state restorations
                    }
                }
                
                // Sync core selections from NES to config
                // The NES.LoadState() already changed the cores, so we need to update config to match
                SyncCoreSelectionsFromNES();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to restore UI settings from state: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Sync config core selections with the actual cores loaded in the NES emulator
        /// This should be called after loading a savestate since the NES cores are changed but config isn't
        /// </summary>
        private void SyncCoreSelectionsFromNES()
        {
            if (nes == null) return;
            
            try
            {
                // Get the actual core IDs from the NES
                string cpuCoreId = nes.GetCpuCoreId();
                string ppuCoreId = nes.GetPpuCoreId();
                string apuCoreId = nes.GetApuCoreId();
                
                // Extract the suffix (e.g., "CPU_FMC" -> "FMC")
                string cpuSuffix = CoreRegistry.ExtractSuffix(cpuCoreId, "CPU_");
                string ppuSuffix = CoreRegistry.ExtractSuffix(ppuCoreId, "PPU_");
                string apuSuffix = CoreRegistry.ExtractSuffix(apuCoreId, "APU_");
                
                // Update config to match
                if (!string.IsNullOrEmpty(cpuSuffix))
                {
                    config.SelectedCpuCore = cpuSuffix;
                    Console.WriteLine($"Synced CPU core to config: {cpuSuffix}");
                }
                
                if (!string.IsNullOrEmpty(ppuSuffix))
                {
                    config.SelectedPpuCore = ppuSuffix;
                    Console.WriteLine($"Synced PPU core to config: {ppuSuffix}");
                }
                
                if (!string.IsNullOrEmpty(apuSuffix))
                {
                    config.SelectedApuCore = apuSuffix;
                    Console.WriteLine($"Synced APU core to config: {apuSuffix}");
                }
                
                // Note: We don't save config here - these are temporary state restorations
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to sync core selections from NES: {ex.Message}");
            }
        }

        private void LoadStateFile(string filePath)
        {
            try
            {
                string stateJson = "";
                string ext = Path.GetExtension(filePath).ToLower();

                if (ext == ".png")
                {
                    using (var bmp = new Bitmap(filePath))
                    {
                        byte[] data = PngPayload.ExtractData(bmp);
                        if (data == null || data.Length == 0)
                        {
                            throw new Exception("No embedded state data found in this image.");
                        }
                        stateJson = Encoding.UTF8.GetString(data);
                    }
                }
                else
                {
                        stateJson = File.ReadAllText(filePath);
                }
                
                // Auto-load ROM if possible
                string savedRomPath = NES.GetSavedRomPath(stateJson);
                string savedRomName = NES.GetSavedRomName(stateJson);
                
                if (nes == null)
                {
                        if (!string.IsNullOrEmpty(savedRomPath) && File.Exists(savedRomPath))
                        {
                            LoadRomFile(savedRomPath);
                        }
                        else
                        {
                            MessageBox.Show($"Cannot load state: No ROM loaded and original ROM path invalid.\nPath: {savedRomPath}", "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                }
                else if (!string.IsNullOrEmpty(savedRomPath) && !string.Equals(savedRomPath, currentRomPath, StringComparison.OrdinalIgnoreCase))
                {
                        // Check if we should auto-switch
                        if (File.Exists(savedRomPath))
                        {
                            LoadRomFile(savedRomPath);
                        }
                        else
                        {
                            var r = MessageBox.Show($"State is for '{savedRomName}' but current ROM is different.\nOriginal path not found: {savedRomPath}\nLoad state anyway?", "ROM Mismatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                            if (r == DialogResult.No) return;
                        }
                }
                
                if (nes == null) return;
                
                // Pause emulation during state load
                bool wasPaused = isPaused;
                isPaused = true;
                
                lock (emulationLock)
                {
                    nes.LoadState(stateJson);
                }
                
                // Restore UI settings (shader config) from state
                RestoreUISettingsFromState(stateJson);
                
                BuildMemoryDomains();
            
                isPaused = wasPaused;
                
                // Update menus to reflect restored settings
                UpdateCoresMenus();
                
                this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)} [State Loaded]";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load state:\n{ex.Message}", "Load State Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadRom_Click(object? sender, EventArgs e)
        {
            using var openFileDialog = new OpenFileDialog
            {
                Filter = "NES ROMs (*.nes)|*.nes|All files (*.*)|*.*",
                Title = "Select a NES ROM"
            };
            
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                LoadRomFile(openFileDialog.FileName);
            }
        }
        
        private void LoadRomFile(string path)
        {
            HideContinueButton();

            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show($"File not found: {path}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                byte[] romData = File.ReadAllBytes(path);
                
                // Stop current emulation if running
                StopEmulation();
                
                // Create new NES instance
                lock (emulationLock)
                {
                    nes = new NES();
                    nes.LoadROM(romData);
                    nes.RomName = Path.GetFileName(path);
                    nes.RomPath = path;
                    InitializeImagineEngine();
                }
                
                currentRomPath = path;
                this.Text = $"BrokenNes - {Path.GetFileName(path)}";
                
                // Add to recent ROMs
                config.AddRecentRom(path);
                
                // Apply saved core selections
                ApplySavedCoreSelections();
                
                // Apply crash behavior
                ApplyCrashBehavior();
                
                // Apply saved null provider
                nes.SetNullProvider(config.SelectedNullProvider);
                
                // Apply image settings (restores user preference for Pixel Perfect)
                ApplyImageSettings();

                // Initialize corruptor domains
                BuildMemoryDomains();
                
                // Apply sound channel settings
                ApplySoundSettings();
                
                // Update cores menus
                UpdateCoresMenus();
                
                // Update recent ROMs menu
                var fileMenu = this.MainMenuStrip?.Items.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Text == "&Emulator");
                if (fileMenu != null)
                {
                    var recentMenu = fileMenu.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Text.Contains("Recent"));
                    if (recentMenu != null)
                    {
                        UpdateRecentRomsMenu(recentMenu);
                    }
                }
                
                // Start emulation on background thread
                StartEmulation();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading ROM: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void CloseRom_Click(object? sender, EventArgs e)
        {
            // Save state if not test rom
            bool isTestRom = nes != null && string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase);
            if (!isTestRom && nes != null)
            {
                SaveContinueState();
            }

            StopEmulation();
            
            lock (emulationLock)
            {
                nes = null;
            }
            
            currentRomPath = string.Empty;
            
            // Clear the display
            if (frameBuffer != null)
            {
                frameBuffer.Clear(unchecked((int)0xFF000000)); // Black
                
                if (useDirectX && dxRenderer?.IsReady == true)
                {
                    dxRenderer.DrawFrame(frameBuffer);
                }
            }
            
            // Return to the static test ROM
            LoadEmbeddedRom();
            
            // Show the continue button so user can resume
            ShowContinueButton();
        }
        
        private void ResetEmulator_Click(object? sender, EventArgs e)
        {
            if (nes != null && !string.IsNullOrEmpty(currentRomPath))
            {
                // Reload the ROM to reset the emulator
                LoadRomFile(currentRomPath);
            }
        }
        
        private void LoadStateFromFile_Click(object? sender, EventArgs e)
        {
            using var openDialog = new OpenFileDialog
            {
                Filter = "State Images (*.png)|*.png|State Files (*.state)|*.state|All Files (*.*)|*.*",
                Title = "Load Save State",
                DefaultExt = "png"
            };
            
            if (openDialog.ShowDialog() == DialogResult.OK)
            {
                LoadStateFile(openDialog.FileName);
            }
        }
        
        private void SaveStateToFile_Click(object? sender, EventArgs e)
        {
            if (nes == null)
            {
                MessageBox.Show("Please load a ROM first.", "No ROM Loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            using var saveDialog = new SaveFileDialog
            {
                Filter = "State Images (*.png)|*.png",
                Title = "Save Save State",
                DefaultExt = "png",
                FileName = Path.GetFileNameWithoutExtension(currentRomPath) + ".png"
            };
            
            if (saveDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    // Pause emulation during state save
                    bool wasPaused = isPaused;
                    isPaused = true;
                    
                    string stateJson;
                    Bitmap screenshot = null;

                    lock (emulationLock)
                    {
                        stateJson = nes.SaveState();
                        screenshot = GetScreenshot();
                    }
                    
                    // Extend state with UI settings (shader config)
                    stateJson = ExtendStateWithUISettings(stateJson);
                    
                    isPaused = wasPaused;

                    if (screenshot == null)
                    {
                         // Fallback if no screenshot available (rare)
                         screenshot = new Bitmap(NES_WIDTH, NES_HEIGHT);
                         using (Graphics g = Graphics.FromImage(screenshot)) g.Clear(Color.Black);
                    }
                    
                    byte[] stateBytes = Encoding.UTF8.GetBytes(stateJson);
                    using (Bitmap embedded = PngPayload.EmbedData(screenshot, stateBytes))
                    {
                        if (embedded == null)
                        {
                             throw new Exception("State data is too large to fit in the screenshot!");
                        }
                        embedded.Save(saveDialog.FileName, ImageFormat.Png);
                    }
                    
                    screenshot.Dispose();

                    this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)} [State Saved]";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save state:\n{ex.Message}", "Save State Error", 
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void TakeScreenshot_Click(object? sender, EventArgs e)
        {
             if (nes == null) return;

             // Capture synchronously to ensure consistency
             Bitmap? screenshot = null;
             string? stateJson = null;
             string localRomPath = currentRomPath;

             lock(emulationLock)
             {
                 try
                 {
                     screenshot = GetScreenshot();
                     stateJson = nes.SaveState();
                 }
                 catch (Exception ex)
                 {
                     Console.WriteLine($"Screenshot capture failed: {ex.Message}");
                     return;
                 }
             }

             if (screenshot == null || stateJson == null) return;

             // Offload IO and processing to background task to avoid hitching
             Task.Run(() => 
             {
                 try
                 {
                     string screenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
                     Directory.CreateDirectory(screenshotsDir);

                     string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff");
                     string filename = $"BrokenNes_{Path.GetFileNameWithoutExtension(localRomPath)}_{timestamp}.png";
                     string fullPath = Path.Combine(screenshotsDir, filename);

                     byte[] stateBytes = Encoding.UTF8.GetBytes(stateJson);
                     
                     using (screenshot)
                     {
                         // Embed state for sharing capabilities
                         using (Bitmap embedded = PngPayload.EmbedData(screenshot, stateBytes))
                         {
                              if (embedded != null)
                              {
                                  embedded.Save(fullPath, ImageFormat.Png);
                              }
                              else
                              {
                                  // If embedding fails (e.g. state too big?), just save the raw screenshot
                                  screenshot.Save(fullPath, ImageFormat.Png);
                              }
                         }
                     }
                     
                     Console.WriteLine($"Screenshot saved: {fullPath}");
                     
                     // Show a brief OSD message or update title
                     if (!this.IsDisposed && this.IsHandleCreated)
                     {
                         this.Invoke((MethodInvoker)delegate 
                         {
                             string currentText = this.Text;
                             // Prevent duplicate status messages
                             string baseText = currentText.Replace(" [Screenshot Saved]", "");
                             this.Text = $"{baseText} [Screenshot Saved]";
                             
                             Task.Delay(1500).ContinueWith(_ => 
                             {
                                 if (this.IsHandleCreated && !this.IsDisposed) 
                                     this.BeginInvoke(new Action(() => this.Text = baseText));
                             });
                         });
                     }

                 }
                 catch (Exception ex)
                 {
                     Console.WriteLine($"Failed to save screenshot: {ex.Message}");
                 }
             });
        }

        private void OpenEmulatorFolder_Click(object? sender, EventArgs e)
        {
            try
            {
                string folderPath = AppDomain.CurrentDomain.BaseDirectory;
                System.Diagnostics.Process.Start("explorer.exe", folderPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open folder: {ex.Message}", "Error");
            }
        }

        private void ShowContinueButton()
        {
            try 
            {
                string continuePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "continue.png");
                if (!File.Exists(continuePath) || continueButton != null) return;

                // Load to memory so we don't lock the file
                Bitmap img;
                using (var fs = new FileStream(continuePath, FileMode.Open, FileAccess.Read))
                {
                    using (var temp = new Bitmap(fs))
                    {
                        img = new Bitmap(temp);
                    }
                }
                    
                using (Graphics g = Graphics.FromImage(img))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                    string text = "Continue?";
                    using (Font f = new Font("Segoe UI", 16, FontStyle.Bold))
                    {
                            // Thicker and blurrier shadow
                            using (var shadowBrush = new SolidBrush(Color.FromArgb(30, Color.Black)))
                            {
                                for (int y = 1; y <= 5; y++)
                                {
                                    for (int x = 1; x <= 5; x++)
                                    {
                                        g.DrawString(text, f, shadowBrush, new PointF(10 + x, 10 + y));
                                    }
                                }
                            }
                             
                            g.DrawString(text, f, Brushes.White, new PointF(10, 10));
                        }

                        // Outline surrounding the continue box
                        using (var outlinePen = new Pen(Color.White, 3))
                        {
                            outlinePen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;
                            g.DrawRectangle(outlinePen, 0, 0, img.Width, img.Height);
                        }
                    }

                continueButton = new PictureBox
                {
                    Image = img,
                    SizeMode = PictureBoxSizeMode.AutoSize,
                    Cursor = Cursors.Hand,
                    Location = new Point(20, 20),
                    BackColor = Color.Transparent
                };
                continueButton.Click += ContinueSession_Click;
                
                displayPanel.Controls.Add(continueButton);
                continueButton.BringToFront();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load continue.png: {ex.Message}");
            }
        }

        private void HideContinueButton()
        {
            if (continueButton != null)
            {
                if (displayPanel.Controls.Contains(continueButton))
                    displayPanel.Controls.Remove(continueButton);
                
                if (continueButton.Image != null) continueButton.Image.Dispose();
                continueButton.Dispose();
                continueButton = null;
            }
        }

        private void ContinueSession_Click(object? sender, EventArgs e)
        {
             HideContinueButton();
             string continuePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "continue.png");
             if (File.Exists(continuePath))
             {
                 LoadStateFile(continuePath);
                 // Delete after loading so it doesn't appear again on next launch unless saved again
                 try { File.Delete(continuePath); } catch {}
             }
        }
        
        private void QuickSaveState_Click(object? sender, EventArgs e)
        {
            if (nes == null)
            {
                MessageBox.Show("Please load a ROM first.", "No ROM Loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            try
            {
                // Pause emulation during state save
                bool wasPaused = isPaused;
                isPaused = true;
                
                lock (emulationLock)
                {
                    quickSaveState = nes.SaveState();
                }
                
                // Extend state with UI settings (shader config)
                quickSaveState = ExtendStateWithUISettings(quickSaveState);
                
                isPaused = wasPaused;
                
                this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)} [Quick Saved]";
                
                // Clear the status message after 2 seconds
                Task.Delay(2000).ContinueWith(_ => 
                {
                    if (this.InvokeRequired)
                        this.Invoke(() => this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)}");
                    else
                        this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)}";
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to quick save state:\n{ex.Message}", "Quick Save Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void QuickLoadState_Click(object? sender, EventArgs e)
        {
            if (nes == null)
            {
                MessageBox.Show("Please load a ROM first.", "No ROM Loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            if (string.IsNullOrEmpty(quickSaveState))
            {
                MessageBox.Show("No quick save state available. Use Quick Save State (F7) first.", 
                    "No Quick Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            
            try
            {
                // Pause emulation during state load
                bool wasPaused = isPaused;
                isPaused = true;
                
                lock (emulationLock)
                {
                    nes.LoadState(quickSaveState);
                }
                
                // Restore UI settings (shader config) from state
                RestoreUISettingsFromState(quickSaveState);
                
                BuildMemoryDomains();
                
                isPaused = wasPaused;
                
                // Update menus to reflect restored settings
                UpdateCoresMenus();
                
                this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)} [Quick Loaded]";
                
                // Clear the status message after 2 seconds
                Task.Delay(2000).ContinueWith(_ => 
                {
                    if (this.InvokeRequired)
                        this.Invoke(() => this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)}");
                    else
                        this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)}";
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to quick load state:\n{ex.Message}", "Quick Load Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void PauseResume_Click(object? sender, EventArgs e)
        {
            if (!isEmulationRunning) return;
            
            isPaused = !isPaused;
            
            if (isPaused)
            {
                audioManager?.Stop();
                this.Text = this.Text + " [PAUSED]";
            }
            else
            {
                audioManager?.Play();
                this.Text = this.Text.Replace(" [PAUSED]", "");
            }
        }
        
        private void About_Click(object? sender, EventArgs e)
        {
            MessageBox.Show(
                "BrokenNes - Windows Edition\n\n" +
                "A NES emulator with multiple CPU, PPU, and APU core options.\n\n" +
                "Controls:\n" +
                "Z - A Button\n" +
                "X - B Button\n" +
                "A - Select\n" +
                "S - Start\n" +
                "Arrow Keys - D-Pad\n" +
                "Space - Pause/Resume",
                "About BrokenNes",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        
        // Config menu event handlers
        private void OpenControllerConfig(int playerNumber)
        {
            var playerConfig = config.GetPlayerController(playerNumber);
            
            using (var configWindow = new ControllerConfigWindow(playerConfig))
            {
                if (configWindow.ShowDialog(this) == DialogResult.OK)
                {
                    // Save the updated configuration
                    config.Save();
                    
                    // Reload input mappings for the configured player
                    if (playerNumber == 1)
                    {
                        if (inputManager != null)
                        {
                            inputManager.SetPlayerConfig(playerConfig);
                        }
                    }
                    else if (playerNumber == 2)
                    {
                        if (inputManager2 == null)
                        {
                            inputManager2 = new InputManager(SharpDX.XInput.UserIndex.Two);
                        }
                        inputManager2.SetPlayerConfig(playerConfig);
                    }
                    
                    MessageBox.Show(
                        $"Player {playerNumber} controller configuration saved!",
                        "Configuration Saved",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            }
        }
        
        // Config menu event handlers
        private void TogglePixelPerfect_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ForcePixelPerfect = menuItem.Checked;
                config.Save();
                ApplyImageSettings();
                UpdateConfigMenus();
            }
        }
        
        private void ToggleNativeAspect_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ForceNativeAspectRatio = menuItem.Checked;
                config.Save();
                ApplyImageSettings();
                UpdateConfigMenus();
            }
        }
        
        private void ToggleNearestNeighbor_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ScalingNearestNeighbor = menuItem.Checked;
                config.Save();
                ApplyImageSettings();
                UpdateConfigMenus();
            }
        }
        
        private void ToggleHideMenuBar_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.HideMenuBarInFullscreen = menuItem.Checked;
                config.Save();
                UpdateConfigMenus();
            }
        }
        
        private void ToggleNoSpeedLimit_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.NoSpeedLimit = menuItem.Checked;
                config.Save();
                UpdateConfigMenus();
                
                // Clear audio buffer to prevent desync
                audioManager?.ClearBuffer();
                
                if (!config.NoSpeedLimit)
                {
                    // Speed limit restored - reset to appropriate speed
                    if (hasSpeedOverride)
                    {
                        audioManager?.SetSpeedMultiplier(speedOverride, preserveBuffer: true);
                    }
                    else
                    {
                        audioManager?.SetSpeedMultiplier(1.0f);
                    }
                }
            }
        }
        
        private void OpenSpeedControl_Click(object? sender, EventArgs e)
        {
            if (speedControlForm == null || speedControlForm.IsDisposed)
            {
                speedControlForm = new SpeedControlForm();
                
                if (inputManager != null)
                {
                    speedControlForm.SetInputManager(inputManager);
                }
                
                speedControlForm.SpeedChanged += SpeedControlForm_SpeedChanged;
                speedControlForm.SpeedChangeComplete += SpeedControlForm_SpeedChangeComplete;
                speedControlForm.FormClosed += (s, args) =>
                {
                    hasSpeedOverride = false;
                    speedOverride = 1.0f;
                    
                    // Reset audio speed and clear buffer to prevent desync
                    audioManager?.SetSpeedMultiplier(1.0f, preserveBuffer: false);
                    audioManager?.ClearBuffer();
                    resetTimingAccumulator = true;
                };
            }
            
            hasSpeedOverride = true;
            resetTimingAccumulator = true;
            speedControlForm.Show(this);
            speedControlForm.Focus();
        }
        
        private void SpeedControlForm_SpeedChanged(object? sender, float speed)
        {
            speedOverride = speed;
            hasSpeedOverride = true;
            
            // Update audio manager immediately for responsive speed changes.
            // Pass preserveBuffer=true to avoid cutting audio during dynamic speed changes (rubber banding effect)
            audioManager?.SetSpeedMultiplier(speed, preserveBuffer: true);
        }
        
        private void SpeedControlForm_SpeedChangeComplete(object? sender, EventArgs e)
        {
            // User released the trackbar - clear audio buffer to resync
            audioManager?.ClearBuffer();
            
            // Reset timing accumulator to prevent fast-forward burst
            resetTimingAccumulator = true;
        }
        
        private void ToggleShowFps_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ShowFps = menuItem.Checked;
                config.Save();
                if (useDirectX && dxRenderer != null)
                {
                    dxRenderer.ShowFps = config.ShowFps;
                }
                UpdateConfigMenus();
            }
        }
        
        private void ToggleVSync_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem && useDirectX && dxRenderer != null)
            {
                config.EnableVSync = menuItem.Checked;
                dxRenderer.EnableVSync = menuItem.Checked;
                config.Save();
                // Note: VSync can reduce performance and may cause stuttering
                // It's off by default for maximum performance
            }
        }
        
        private void ToggleProfiling_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ProfilingEnabled = menuItem.Checked;
                PerformanceProfiler.Enabled = menuItem.Checked;
                config.Save();
                
                if (menuItem.Checked)
                {
                    PerformanceProfiler.Reset();
                    Console.WriteLine("Performance profiling started");
                }
                else
                {
                    Console.WriteLine("Performance profiling stopped");
                }
            }
        }
        
        private void ToggleAutoScrambleCores_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.AutoScrambleCores = menuItem.Checked;
                config.Save();
                
                if (autoScrambleTimer != null)
                {
                    if (menuItem.Checked)
                    {
                        autoScrambleTimer.Start();
                        Console.WriteLine("Auto-scramble cores started (420ms interval)");
                    }
                    else
                    {
                        autoScrambleTimer.Stop();
                        Console.WriteLine("Auto-scramble cores stopped");
                    }
                }
                
                UpdateConfigMenus();
            }
        }
        
        private void ToggleShowConsole_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ShowConsole = menuItem.Checked;
                config.Save();
                
                Program.SetConsoleVisibility(config.ShowConsole);
                
                Console.WriteLine($"Console visibility set to: {config.ShowConsole}");
                
                UpdateConfigMenus();
            }
        }

        private void OpenRtcTool_Click(object? sender, EventArgs e)
        {
            if (rtcForm == null || rtcForm.IsDisposed)
            {
                rtcForm = new RealTimeCorruptorForm(this);
                rtcForm.FormClosed += (_, _) => rtcForm = null;
            }

            rtcForm.Show(this);
            rtcForm.Focus();
        }

        private void OpenGhTool_Click(object? sender, EventArgs e)
        {
            if (ghForm == null || ghForm.IsDisposed)
            {
                ghForm = new GlitchHarvesterForm(this);
                ghForm.FormClosed += (_, _) => ghForm = null;
            }

            ghForm.Show(this);
            ghForm.Focus();
        }

        private void OpenImagineTool_Click(object? sender, EventArgs e)
        {
            if (imagineForm == null || imagineForm.IsDisposed)
            {
                imagineForm = new ImagineForm(this);
                imagineForm.FormClosed += (_, _) => imagineForm = null;
            }

            imagineForm.Show(this);
            imagineForm.Focus();
        }

        private void OpenHexEditor_Click(object? sender, EventArgs e)
        {
            if (hexEditorForm == null || hexEditorForm.IsDisposed)
            {
                hexEditorForm = new HexEditorForm(this);
                hexEditorForm.FormClosed += (_, _) => hexEditorForm = null;
            }

            hexEditorForm.Show(this);
            hexEditorForm.Focus();
        }
        
        private void SetCrashBehavior(string behavior)
        {
            config.CrashBehavior = behavior;
            config.Save();
            
            // Apply to current NES instance if one is running
            ApplyCrashBehavior();
            
            UpdateConfigMenus();
            
            Console.WriteLine($"Crash behavior set to: {behavior}");
        }
        
        private void SetBackground(string backgroundName)
        {
            config.SelectedBackground = backgroundName;
            config.Save();
            
            // Apply to DirectX renderer if available
            if (useDirectX && dxRenderer != null)
            {
                dxRenderer.SetBackground(backgroundName);
            }
            
            UpdateConfigMenus();
            
            Console.WriteLine($"Background set to: {backgroundName}");
        }
        
        private void SetNullProvider(string providerName)
        {
            config.SelectedNullProvider = providerName;
            config.Save();
            
            // Apply to current NES instance if one is running
            if (nes != null)
            {
                nes.SetNullProvider(providerName);
            }
            
            UpdateConfigMenus();
            
            Console.WriteLine($"Null provider set to: {providerName}");
        }
        
        private void ToggleScanlines_Click(object sender, EventArgs e)
        {
            config.RenderScanlines = !config.RenderScanlines;
            config.Save();
            
            // Apply to DirectX renderer if available
            if (useDirectX && dxRenderer != null)
            {
                dxRenderer.RenderScanlines = config.RenderScanlines;
            }
            
            UpdateConfigMenus();
            Console.WriteLine($"Render Scanlines: {config.RenderScanlines}");
        }
        
        private void ToggleViewportShadow_Click(object sender, EventArgs e)
        {
            config.RenderViewportShadow = !config.RenderViewportShadow;
            config.Save();
            
            // Apply to DirectX renderer if available
            if (useDirectX && dxRenderer != null)
            {
                dxRenderer.RenderViewportShadow = config.RenderViewportShadow;
            }
            
            UpdateConfigMenus();
            Console.WriteLine($"Render Viewport Shadow: {config.RenderViewportShadow}");
        }
        
        private void ApplyCrashBehavior()
        {
            if (nes == null) return;
            
            try
            {
                lock (emulationLock)
                {
                    if (nes != null)
                    {
                        switch (config.CrashBehavior)
                        {
                            case "IgnoreErrors":
                                nes.SetCrashBehavior(NES.CrashBehavior.IgnoreErrors);
                                break;
                            case "ImagineFix":
                                nes.SetCrashBehavior(NES.CrashBehavior.ImagineFix);
                                nes.SetStubbornFixEnabled(corruptor.StubbornMode);
                                break;
                            default: // "RedScreen"
                                nes.SetCrashBehavior(NES.CrashBehavior.RedScreen);
                                break;
                        }
                        if (imagineEngine != null)
                        {
                            nes.ImagineShot = pc =>
                            {
                                try { imagineEngine.ImagineFromPc(pc, Math.Clamp(corruptor.CorruptIntensity, 1, 32)); }
                                catch (Exception ex) { Console.WriteLine($"ImagineShot error: {ex.Message}"); }
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error applying crash behavior: {ex.Message}");
            }
        }

        private void InitializeImagineEngine()
        {
            if (nes == null) return;
            imagineEngine = new ImagineEngine(nes, corruptor);
            corruptor.EmulatorHooks = imagineEngine;
            nes.ImagineShot = pc =>
            {
                try { imagineEngine.ImagineFromPc(pc, Math.Clamp(corruptor.CorruptIntensity, 1, 32)); }
                catch (Exception ex) { Console.WriteLine($"ImagineShot error: {ex.Message}"); }
            };
            nes.SetStubbornFixEnabled(corruptor.StubbornMode);
        }

        internal void SetStubbornMode(bool enabled)
        {
            lock (corruptorLock) { corruptor.StubbornMode = enabled; }
            if (nes != null)
            {
                QueueEmuAction(() =>
                {
                    try { nes.SetStubbornFixEnabled(enabled); }
                    catch (Exception ex) { Console.WriteLine($"SetStubbornFixEnabled error: {ex.Message}"); }
                });
            }
            NotifyCorruptorChanged();
        }

        private void BuildMemoryDomains()
        {
            if (nes == null) return;
            lock (corruptorLock)
            {
                corruptor.MemoryDomains.Clear();
                try
                {
                    corruptor.MemoryDomains.Add(new DomainSel { Key = "PRG", Label = "PRG ROM", Size = GetApproxSize(i => nes.PeekPrg(i)), Selected = false });
                    corruptor.MemoryDomains.Add(new DomainSel { Key = "PRGRAM", Label = "PRG RAM", Size = GetApproxSize(i => nes.PeekPrgRam(i)), Selected = false });
                    corruptor.MemoryDomains.Add(new DomainSel { Key = "CHR", Label = "CHR", Size = GetApproxSize(i => nes.PeekChr(i)), Selected = false });
                    corruptor.MemoryDomains.Add(new DomainSel { Key = "RAM", Label = "System RAM", Size = 2048, Selected = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"BuildMemoryDomains error: {ex.Message}");
                }
            }
            NotifyCorruptorChanged();
        }

        private int GetApproxSize(Func<int, byte> peek)
        {
            int size = 1024;
            int lastNonZero = 0;
            for (int i = 0; i < size; i += 128)
            {
                if (peek(i) != 0) lastNonZero = i;
            }
            for (int i = 1024; i <= 512 * 1024; i *= 2)
            {
                byte v = peek(i - 1);
                if (v != 0) lastNonZero = i - 1;
                else { size = i; break; }
            }
            return Math.Max((lastNonZero + 256) & ~255, 0);
        }

        internal void RefreshMemoryDomainsRequested()
        {
            if (!IsEmulatorReady) return;
            QueueEmuAction(BuildMemoryDomains);
        }

        internal int GetMemoryDomainSize(string domainKey)
        {
            lock (corruptorLock)
            {
                var domain = corruptor.MemoryDomains.FirstOrDefault(d => string.Equals(d.Key, domainKey, StringComparison.OrdinalIgnoreCase));
                return domain?.Size ?? 0;
            }
        }

        internal Task<byte[]> ReadMemoryAsync(string domainKey, int start, int length)
        {
            if (nes == null || length <= 0)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            start = Math.Max(0, start);
            int domainSize = GetMemoryDomainSize(domainKey);
            if (domainSize > 0)
            {
                length = Math.Min(length, Math.Max(0, domainSize - start));
            }

            if (length <= 0)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            return RunOnEmulationThreadAsync(() =>
            {
                var buffer = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    buffer[i] = PeekDomainByte(domainKey, start + i);
                }
                return buffer;
            });
        }

        internal Task WriteMemoryAsync(string domainKey, int address, byte value)
        {
            if (nes == null)
            {
                return Task.CompletedTask;
            }

            address = Math.Max(0, address);
            int domainSize = GetMemoryDomainSize(domainKey);
            if (domainSize > 0 && address >= domainSize)
            {
                return Task.CompletedTask;
            }

            return RunOnEmulationThreadAsync(() => PokeDomainByte(domainKey, address, value));
        }

        private byte PeekDomainByte(string domainKey, int address)
        {
            if (nes == null || address < 0)
            {
                return 0;
            }

            return domainKey switch
            {
                "PRG" => nes.PeekPrg(address),
                "PRGRAM" => nes.PeekPrgRam(address),
                "CHR" => nes.PeekChr(address),
                "RAM" => nes.PeekSystemRam(address),
                _ => nes.PeekSystemRam(address)
            };
        }

        private void PokeDomainByte(string domainKey, int address, byte value)
        {
            if (nes == null || address < 0)
            {
                return;
            }

            switch (domainKey)
            {
                case "PRG":
                    nes.PokePrg(address, value);
                    break;
                case "PRGRAM":
                    nes.PokePrgRam(address, value);
                    break;
                case "CHR":
                    nes.PokeChr(address, value);
                    break;
                case "RAM":
                    nes.PokeSystemRam(address, value);
                    break;
            }
        }

        internal void SetCorruptIntensity(int value)
        {
            lock (corruptorLock) { corruptor.OnIntensityChange(value); }
            NotifyCorruptorChanged();
        }

        internal void SetBlastType(string blastType)
        {
            lock (corruptorLock) { corruptor.OnBlastTypeChanged(blastType); }
            NotifyCorruptorChanged();
        }

        internal void SetSelectedDomains(IEnumerable<string> keys)
        {
            lock (corruptorLock) { corruptor.DomainsChanged(keys); }
            NotifyCorruptorChanged();
        }

        internal void SetAutoCorrupt(bool enabled)
        {
            lock (corruptorLock)
            {
                corruptor.AutoCorrupt = enabled;
                corruptor.LastBlastInfo = enabled ? "Auto-corrupt enabled" : "Auto-corrupt disabled";
            }
            NotifyCorruptorChanged();
        }

        internal void RequestBlast()
        {
            if (!IsEmulatorReady) return;
            QueueEmuAction(() =>
            {
                if (nes == null) return;
                lock (corruptorLock)
                {
                    corruptor.Blast(nes);
                }
                NotifyCorruptorChanged();
            });
        }

        internal void RequestLetItRip()
        {
            lock (corruptorLock) { corruptor.LetItRip(); }
            RefreshMemoryDomainsRequested();
            NotifyCorruptorChanged();
        }

        internal void SetCrashBehaviorFromTools(string behavior) => SetCrashBehavior(behavior);
        
        private void AutoScrambleTimer_Tick(object? sender, EventArgs e)
        {
            if (nes == null || !isEmulationRunning) return;
            
            try
            {
                RandomScrambleCore();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during auto-scramble: {ex.Message}");
            }
        }
        
        private void RandomScrambleCore()
        {
            // Randomly choose which core type to scramble (0=CPU, 1=PPU, 2=APU, 3=Shader)
            int coreType = scrambleRandom.Next(0, 4);
            
            switch (coreType)
            {
                case 0: // CPU
                    {
                        var cpuCores = CoreRegistry.CpuIds;
                        if (cpuCores.Count > 1)
                        {
                            var randomCore = cpuCores[scrambleRandom.Next(cpuCores.Count)];
                            Console.WriteLine($"Auto-scramble: Switching CPU to {randomCore}");
                            SetCpuCore(randomCore);
                        }
                    }
                    break;
                    
                case 1: // PPU
                    {
                        var ppuCores = CoreRegistry.PpuIds;
                        if (ppuCores.Count > 1)
                        {
                            var randomCore = ppuCores[scrambleRandom.Next(ppuCores.Count)];
                            Console.WriteLine($"Auto-scramble: Switching PPU to {randomCore}");
                            SetPpuCore(randomCore);
                        }
                    }
                    break;
                    
                case 2: // APU
                    {
                        var apuCores = CoreRegistry.ApuIds;
                        if (apuCores.Count > 1)
                        {
                            var randomCore = apuCores[scrambleRandom.Next(apuCores.Count)];
                            Console.WriteLine($"Auto-scramble: Switching APU to {randomCore}");
                            SetApuCore(randomCore);
                        }
                    }
                    break;
                    
                case 3: // Shader
                    {
                        if (useDirectX && dxRenderer != null && config.ShadersEnabled)
                        {
                            var availableShaders = NesDirectXRenderer.GetAvailableShaders().ToList();
                            if (availableShaders.Count > 1)
                            {
                                var randomShader = availableShaders[scrambleRandom.Next(availableShaders.Count)];
                                Console.WriteLine($"Auto-scramble: Switching Shader to {randomShader}");
                                NesShaderControl.SwitchShader(randomShader);
                                config.CurrentShader = randomShader;
                                config.Save();
                            }
                        }
                    }
                    break;
            }
        }
        
        private void SetWindowZoom(int zoom)
        {
            config.WindowZoom = zoom;
            config.Save();
            
            // Calculate new window size based on NES resolution and zoom
            int newWidth = NES_WIDTH * zoom;
            int newHeight = NES_HEIGHT * zoom;
            
            // Add space for menu bar (approximate)
            int menuHeight = this.MainMenuStrip?.Height ?? 24;
            
            this.ClientSize = new Size(newWidth, newHeight + menuHeight);
            
            // Update menu checkmarks
            UpdateConfigMenus();
        }
        
        private void ToggleSoundChannel(int channelIndex)
        {
            // Toggle the bit for this channel
            int mask = 1 << channelIndex;
            config.EnabledChannels ^= mask;
            config.Save();
            
            // Apply to NES if available
            ApplySoundSettings();
            
            // Update menu checkmarks
            UpdateConfigMenus();
        }
        
        private void SetSoundQuality(int sampleRate)
        {
            config.SoundQuality = sampleRate;
            config.Save();
            
            // Reinitialize audio manager with new settings
            ReinitializeAudio();
            
            // Update menu checkmarks
            UpdateConfigMenus();
        }
        
        private void SetSoundBuffer(int bufferSize)
        {
            config.SoundBuffer = bufferSize;
            config.Save();
            
            // Reinitialize audio manager with new settings
            ReinitializeAudio();
            
            // Update menu checkmarks
            UpdateConfigMenus();
        }
        
        private void ReinitializeAudio()
        {
            try
            {
                bool wasPlaying = audioManager?.IsPlaying ?? false;
                
                // Dispose old audio manager
                audioManager?.Dispose();
                
                // Create new audio manager with updated settings
                audioManager = new AudioManager(sampleRate: config.SoundQuality, channels: 1);
                
                // Resume playback if it was playing
                if (wasPlaying && isEmulationRunning && !isPaused)
                {
                    audioManager.Play();
                }
                
                Console.WriteLine($"Audio reinitialized: {config.SoundQuality}Hz, buffer={config.SoundBuffer}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to reinitialize audio: {ex.Message}");
                MessageBox.Show($"Audio reinitialization failed: {ex.Message}\n\nThe emulator will continue without sound.",
                    "Audio Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        
        private void ApplyImageSettings()
        {
            if (dxRenderer != null && useDirectX)
            {
                // Apply pixel perfect setting
                // Force Pixel Perfect if running the embedded Test ROM (Null Provider)
                bool isTestRom = nes != null && string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase);
                dxRenderer.PixelPerfect = isTestRom || config.ForcePixelPerfect;
                
                // Apply interpolation mode based on ScalingNearestNeighbor
                dxRenderer.InterpolationMode = config.ScalingNearestNeighbor 
                    ? SharpDX.Direct2D1.BitmapInterpolationMode.NearestNeighbor
                    : SharpDX.Direct2D1.BitmapInterpolationMode.Linear;
                
                // Apply aspect ratio setting
                dxRenderer.ForceNativeAspectRatio = config.ForceNativeAspectRatio;
                
                // Apply FPS display setting
                dxRenderer.ShowFps = config.ShowFps;
                
                // Apply background effects settings
                dxRenderer.RenderScanlines = config.RenderScanlines;
                dxRenderer.RenderViewportShadow = config.RenderViewportShadow;
                
                // Force a redraw
                if (frameBuffer != null)
                {
                    dxRenderer.DrawFrame(frameBuffer);
                }
            }
        }
        
        private void ApplySoundSettings()
        {
            if (nes != null)
            {
                // Apply channel enable/disable to the APU via the NES bus
                try
                {
                    // Get the APU through reflection or a public method
                    var busField = nes.GetType().GetField("bus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (busField != null)
                    {
                        var bus = busField.GetValue(nes);
                        if (bus != null)
                        {
                            var busType = bus.GetType();

                            void ApplyMask(IAPU? apuInstance)
                            {
                                apuInstance?.SetEnabledChannels(config.EnabledChannels);
                            }

                            // Prefer a public property if one exists (future-proof), else fall back to fields.
                            var apuFromProperty = busType.GetProperty("Apu", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(bus) as IAPU;
                            ApplyMask(apuFromProperty);

                            // Common fields on Bus: apu, activeApu, apuJank, apuQN
                            var apuField = busType.GetField("apu", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(bus) as IAPU;
                            var activeApuField = busType.GetField("activeApu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(bus) as IAPU;
                            var apuJankField = busType.GetField("apuJank", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(bus) as IAPU;
                            var apuQnField = busType.GetField("apuQN", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(bus) as IAPU;
                            ApplyMask(apuField);
                            ApplyMask(activeApuField);
                            ApplyMask(apuJankField);
                            ApplyMask(apuQnField);

                            // Also push the mask to any cached APU instances to keep future hot-swaps consistent.
                            var apuInstancesField = busType.GetField("_apuInstances", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (apuInstancesField?.GetValue(bus) is System.Collections.Generic.IDictionary<string, IAPU> apuDict)
                            {
                                foreach (var apu in apuDict.Values)
                                {
                                    ApplyMask(apu);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error applying sound settings: {ex.Message}");
                }
            }
        }
        
        private void StartEmulation()
        {
            if (nes == null) return;
            
            // Ensure form has focus so keyboard input works immediately
            this.Activate(); 
            this.Focus();
            
            isEmulationRunning = true;
            isPaused = false;
            
            // Start emulation thread
            emulatorThread = new Thread(EmulationThreadProc)
            {
                Name = "NES Emulation Thread",
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true
            };
            emulatorThread.Start();
            
            // Start render timer on UI thread
            audioManager?.Play();
        }
        
        private void StopEmulation()
        {
            isEmulationRunning = false;
            
            // Wait for emulation thread to finish
            if (emulatorThread != null && emulatorThread.IsAlive)
            {
                emulatorThread.Join(1000);
            }
            
            audioManager?.Stop();
        }
        
        private void EmulationThreadProc()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            const double targetFrameTime = 1.0 / 60.0; // 60 FPS
            double accumulator = 0;
            int framesSinceReport = 0;
            var reportStopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Initialize FPS tracking for audio speed adjustment
            fpsStopwatch = System.Diagnostics.Stopwatch.StartNew();
            fpsFrameCount = 0;
            
            // Audio-driven timing: target buffer level in ms
            const int TargetAudioBufferMs = 60;  // Sweet spot: not too laggy, not too tight
            const int MinAudioBufferMs = 30;     // Run more frames if below this
            const int MaxAudioBufferMs = 100;    // Skip frames if above this
            
            // High-resolution timer for more precise frame timing
            long ticksPerSecond = System.Diagnostics.Stopwatch.Frequency;
            long targetFrameTicks = (long)(ticksPerSecond / 60.0);
            long nextFrameTime = stopwatch.ElapsedTicks;
            
            while (isEmulationRunning)
            {
                while (emulationActions.TryDequeue(out var act))
                {
                    try { act(); }
                    catch (Exception ex) { Console.WriteLine($"Emu action error: {ex.Message}"); }
                }

                using (PerformanceProfiler.Time("Frame"))
                {
                    if (isPaused)
                    {
                        Thread.Sleep(10);
                        stopwatch.Restart();
                        nextFrameTime = stopwatch.ElapsedTicks;
                        accumulator = 0;
                        continue;
                    }
                    
                    // Check if we need to reset timing (after speed change)
                    if (resetTimingAccumulator)
                    {
                        accumulator = 0;
                        stopwatch.Restart();
                        nextFrameTime = stopwatch.ElapsedTicks;
                        resetTimingAccumulator = false;
                    }
                
                // Get current audio buffer level to guide timing
                int audioBufferMs = audioManager?.GetBufferedDurationMs() ?? TargetAudioBufferMs;
                
                // Determine how many frames to run based on audio buffer state
                int framesToRun = 0;
                
                if (config.NoSpeedLimit)
                {
                    // Run as fast as possible - always run at least one frame
                    framesToRun = 1;
                }
                else if (hasSpeedOverride)
                {
                    // Speed override - use time-based accumulator
                    double deltaTime = stopwatch.Elapsed.TotalSeconds;
                    stopwatch.Restart();
                    accumulator += deltaTime;
                    double effectiveFrameTime = targetFrameTime / speedOverride;
                    framesToRun = 0;
                    while (accumulator >= effectiveFrameTime)
                    {
                        framesToRun++;
                        accumulator -= effectiveFrameTime;
                    }
                    
                    // For slow-motion, add a small sleep when no frames need to run
                    if (framesToRun == 0)
                    {
                        Thread.Sleep(1); // Small sleep to prevent CPU spinning
                    }
                }
                else
                {
                    // AUDIO-DRIVEN TIMING: Let audio buffer level guide frame production
                    // This prevents the doppler effect and audio pops
                    
                    long now = stopwatch.ElapsedTicks;
                    
                    if (audioBufferMs < MinAudioBufferMs)
                    {
                        // Audio buffer is getting low - produce frames immediately!
                        // This prevents underrun pops
                        framesToRun = 2; // Catch up quickly
                        nextFrameTime = now + targetFrameTicks;
                    }
                    else if (audioBufferMs > MaxAudioBufferMs)
                    {
                        // Audio buffer is too full - skip this cycle
                        // This prevents audio lag without dropping samples
                        framesToRun = 0;
                        nextFrameTime = now + targetFrameTicks / 2; // Check again soon
                    }
                    else if (now >= nextFrameTime)
                    {
                        // Normal case: time for a new frame
                        framesToRun = 1;
                        
                        // Adjust timing slightly based on buffer level to stay centered
                        if (audioBufferMs < TargetAudioBufferMs)
                        {
                            // Running a bit behind, speed up slightly
                            nextFrameTime = now + (long)(targetFrameTicks * 0.95);
                        }
                        else if (audioBufferMs > TargetAudioBufferMs + 20)
                        {
                            // Running a bit ahead, slow down slightly
                            nextFrameTime = now + (long)(targetFrameTicks * 1.05);
                        }
                        else
                        {
                            // Buffer is at ideal level
                            nextFrameTime = now + targetFrameTicks;
                        }
                    }
                    else
                    {
                        // Not yet time for next frame
                        framesToRun = 0;
                    }
                }
                
                // Run the calculated number of frames
                for (int f = 0; f < framesToRun && isEmulationRunning && !isPaused; f++)
                {
                    if (nes != null)
                    {
                        try
                        {
                            // Poll input managers for both players and update NES button states
                            using (PerformanceProfiler.Time("Input.Poll"))
                            {
                                bool[] p1Inputs = new bool[8];
                                bool[] p2Inputs = new bool[8];
                                
                                if (inputManager != null)
                                {
                                    inputManager.Poll();
                                    for (int i = 0; i < 8; i++)
                                    {
                                        p1Inputs[i] = inputManager.GetButton(i);
                                    }
                                }
                                
                                if (inputManager2 != null)
                                {
                                    inputManager2.Poll();
                                    for (int i = 0; i < 8; i++)
                                    {
                                        p2Inputs[i] = inputManager2.GetButton(i);
                                    }
                                }
                                
                                nes.SetInputs(p1Inputs, p2Inputs);
                            }
                            
                            // Enable static for test.nes ROM (like in web version)
                            bool isTestRom = string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase);
                            nes.EnableStatic(isTestRom);
                            
                            // Run one frame of emulation
                            using (PerformanceProfiler.Time("NES.RunFrame"))
                            {
                                nes.RunFrame();
                            }

                            if (corruptor.AutoCorrupt)
                            {
                                lock (corruptorLock)
                                {
                                    try { corruptor.Blast(nes); }
                                    catch (Exception ex) { Console.WriteLine($"Auto-corrupt error: {ex.Message}"); }
                                }
                            }
                            
                            // Track FPS for display and audio speed adjustment
                            fpsFrameCount++;
                            if (fpsStopwatch != null && fpsStopwatch.Elapsed.TotalSeconds >= 0.5)
                            {
                                currentFps = fpsFrameCount / fpsStopwatch.Elapsed.TotalSeconds;
                                fpsFrameCount = 0;
                                fpsStopwatch.Restart();
                                
                                // During no speed limit, adjust audio speed based on actual FPS
                                if (config.NoSpeedLimit)
                                {
                                    float actualSpeed = (float)(currentFps / 60.0);
                                    audioManager?.SetSpeedMultiplier(actualSpeed);
                                }
                            }
                            
                            // Process audio samples from APU
                            if (audioManager != null)
                            {
                                using (PerformanceProfiler.Time("Audio.Queue"))
                                {
                                    try
                                    {
                                        float[] audioSamples = nes.GetAudioBuffer();
                                        if (audioSamples != null && audioSamples.Length > 0)
                                        {
                                            audioManager.QueueSamples(audioSamples);
                                        }
                                    }
                                    catch (Exception audioEx)
                                    {
                                        Console.WriteLine($"Audio error: {audioEx.Message}");
                                    }
                                }
                            }
                            
                            // Get the framebuffer from the PPU and copy to backbuffer
                            // Only lock when actually copying to reduce contention
                            using (PerformanceProfiler.Time("FrameBuffer.Copy"))
                            {
                                byte[]? nesFrameBuffer = nes.GetFrameBuffer();
                                if (nesFrameBuffer != null && nesFrameBuffer.Length == NES_WIDTH * NES_HEIGHT * 4 && backBuffer != null)
                                {
                                    lock (emulationLock)
                                    {
                                        backBuffer.CopyFromBytes(nesFrameBuffer);
                                    }
                                    
                                    // Render immediately after frame is ready (marshal to UI thread)
                                    if (InvokeRequired)
                                    {
                                        BeginInvoke(new Action(RenderFrame));
                                    }
                                    else
                                    {
                                        RenderFrame();
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Emulation error: {ex.Message}");
                            Console.WriteLine($"Stack trace: {ex.StackTrace}");
                            isEmulationRunning = false;
                        }
                    }
                }
                
                // Check if emulator has crashed and throttle appropriately
                if (nes != null && nes.IsCrashed())
                {
                    // When crashed, cap at 60 FPS to prevent UI thread flooding
                    Thread.Sleep(16); // ~60 FPS
                    accumulator = 0; // Reset accumulator to prevent catchup frames
                    
                    // Render the crash screen once per frame
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(RenderFrame));
                    }
                    else
                    {
                        RenderFrame();
                    }
                    continue; // Skip normal throttling logic
                }
                
                // Save periodic profiling report every 10 seconds
                framesSinceReport++;
                if (PerformanceProfiler.Enabled && reportStopwatch.Elapsed.TotalSeconds >= 10.0)
                {
                    var reportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"performance_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    PerformanceProfiler.SaveReport(reportPath);
                    reportStopwatch.Restart();
                    framesSinceReport = 0;
                }
                
                // Precision wait using spin-wait for final microseconds
                // This avoids Windows timer granularity issues
                if (!config.NoSpeedLimit && !hasSpeedOverride && framesToRun == 0)
                {
                    long now = stopwatch.ElapsedTicks;
                    long ticksToWait = nextFrameTime - now;
                    
                    if (ticksToWait > 0)
                    {
                        // If more than 2ms to wait, sleep for most of it
                        double msToWait = (double)ticksToWait / ticksPerSecond * 1000.0;
                        if (msToWait > 2)
                        {
                            Thread.Sleep((int)(msToWait - 1));
                        }
                        
                        // Spin-wait for remaining time (sub-millisecond precision)
                        while (stopwatch.ElapsedTicks < nextFrameTime)
                        {
                            Thread.SpinWait(10);
                        }
                    }
                }
                else if (config.NoSpeedLimit)
                {
                    // Tiny yield to prevent complete CPU lockup but still run fast
                    Thread.Sleep(0);
                }
                } // End of PerformanceProfiler.Time("Frame") using block
            }
        }
        
        private void RenderFrame()
        {
            if (backBuffer == null || frameBuffer == null || !useDirectX || dxRenderer?.IsReady != true) return;
            
            using (PerformanceProfiler.Time("RenderFrame"))
            {
                try
                {
                    // Copy backBuffer to frameBuffer and render
                    lock (emulationLock)
                    {
                        Array.Copy(backBuffer.Bits, frameBuffer.Bits, backBuffer.Bits.Length);
                    }
                    
                    // Capture current inputs for display
                    bool[] currentInputs = new bool[8];
                    if (inputManager != null)
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            currentInputs[i] = inputManager.GetButton(i);
                        }
                    }
                    
                    // Render using DirectX
                    using (PerformanceProfiler.Time("DirectX.DrawFrame"))
                    {
                        dxRenderer.DrawFrame(frameBuffer, currentInputs);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Render error: {ex.Message}");
                }
            }
        }
        
        private void EmulatorTimer_Tick(object? sender, EventArgs e)
        {
            if (nes == null || frameBuffer == null) return;
            
            try
            {
                // Run one frame of emulation
                nes.RunFrame();
                
                // Process audio samples from APU
                if (audioManager != null)
                {
                    try
                    {
                        float[] audioSamples = nes.GetAudioBuffer();
                        if (audioSamples != null && audioSamples.Length > 0)
                        {
                            audioManager.QueueSamples(audioSamples);
                        }
                    }
                    catch (Exception audioEx)
                    {
                        Console.WriteLine($"Audio error: {audioEx.Message}");
                    }
                }
                
                // Get the framebuffer from the PPU
                byte[]? nesFrameBuffer = nes.GetFrameBuffer();
                
                if (nesFrameBuffer != null && nesFrameBuffer.Length == NES_WIDTH * NES_HEIGHT * 4)
                {
                    // Copy framebuffer data to DirectBitmap (convert byte[] to expected format)
                    frameBuffer.CopyFromBytes(nesFrameBuffer);
                    
                    // Render using DirectX if available
                    if (useDirectX && dxRenderer?.IsReady == true)
                    {
                        dxRenderer.DrawFrame(frameBuffer);
                    }
                }
            }
            catch (Exception ex)
            {
                isEmulationRunning = false;
                Console.WriteLine($"Emulation error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                MessageBox.Show($"Emulation error: {ex.Message}\n\nStack trace:\n{ex.StackTrace}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void MainForm_KeyDown(object? sender, KeyEventArgs e)
        {
            // Handle Alt+Enter for fullscreen toggle
            if (e.Alt && e.KeyCode == Keys.Enter)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            
            if (inputManager != null)
            {
                inputManager.OnKeyDown(e.KeyCode);
                e.Handled = true;
            }
        }
        
        private void MainForm_KeyUp(object? sender, KeyEventArgs e)
        {
            if (inputManager != null)
            {
                inputManager.OnKeyUp(e.KeyCode);
                e.Handled = true;
            }
        }
        
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ShowContinueButton();
        }

        private void SaveContinueState()
        {
             if (nes == null) return;
             
             string continuePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "continue.png");
             
             try
             {
                 lock(emulationLock)
                 {
                     // Synchronous capture
                     using (var screenshot = GetScreenshot())
                     {
                         string stateJson = nes.SaveState();
                         if (screenshot != null && stateJson != null)
                         {
                             byte[] stateBytes = Encoding.UTF8.GetBytes(stateJson);
                             using (Bitmap embedded = PngPayload.EmbedData(screenshot, stateBytes))
                             {
                                 embedded?.Save(continuePath, ImageFormat.Png);
                             }
                             Console.WriteLine("Game saved to continue.png");
                         }
                     }
                 }
             }
             catch (Exception ex) 
             {
                 Console.WriteLine("Failed to save continue state: " + ex.Message);
             }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Check if we're on the test ROM
            bool isTestRom = nes != null && string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase);
            
            if (!isTestRom && nes != null && !string.IsNullOrEmpty(currentRomPath))
            {
                // Auto-save "continue.png" on exit for non-test ROMs
                SaveContinueState();
            }

            StopEmulation();
            audioManager?.Dispose();
            inputManager?.Dispose();
            frameBuffer?.Dispose();
            backBuffer?.Dispose();
            dxRenderer?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
