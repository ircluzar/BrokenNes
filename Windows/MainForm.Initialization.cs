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
using BrokenNes.Windows.WebApi;
using PngPayloadEmbedding;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
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
            
            var webModeItem = new ToolStripMenuItem("Web Mode (Test Page)", null, (s, e) => {
                // Load the webmodules index page
                string webmodulesIndexUri = $"https://{WebModuleManager.SharedVirtualHostName}/index.html";
                Helpers.WebViewHelper.NavigateToUri(webView, webmodulesIndexUri);
                SwitchViewMode(ViewMode.Web);
            });
            webModeItem.ShortcutKeys = Keys.Control | Keys.D3;
            webMenu.DropDownItems.Add(webModeItem);
            
            var overlayModeItem = new ToolStripMenuItem("Overlay Mode", null, (s, e) => SwitchViewMode(ViewMode.Overlay));
            overlayModeItem.ShortcutKeys = Keys.Control | Keys.D4;
            webMenu.DropDownItems.Add(overlayModeItem);
            
            // Add separator before webmodules
            webMenu.DropDownItems.Add(new ToolStripSeparator());
            
            // Discover and add web modules
            var webModules = WebModuleManager.DiscoverModules();
            if (webModules.Length > 0)
            {
                foreach (var module in webModules)
                {
                    var moduleItem = new ToolStripMenuItem(module.Name, null, (s, e) => LoadWebModule(module));
                    webMenu.DropDownItems.Add(moduleItem);
                }
            }
            else
            {
                var noModulesItem = new ToolStripMenuItem("(No modules available)")
                {
                    Enabled = false
                };
                webMenu.DropDownItems.Add(noModulesItem);
            }
            
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
                // Reuse existing WebView2 instance whenever possible
                if (webView != null && !webView.IsDisposed)
                {
                    // Ensure it is in the controls collection
                    if (!this.Controls.Contains(webView))
                    {
                        this.Controls.Add(webView);
                    }
                }
                else
                {
                    // If we have a reference but it's disposed or null, create a new one
                    // Note: If webView was not null, we are replacing the reference. 
                    // The previous object is either disposed (per check) or we should ensure we don't leak.
                    
                    // Explicitly null out old reference if it existed (though we know it's unusable)
                    if (webView != null)
                    {
                         webView.Dispose(); // Safety dispose
                         webView = null;
                    }

                    webView = Helpers.WebViewHelper.CreateWebView(this);
                    
                    // Initialize WebView2 asynchronously
                    Helpers.WebViewHelper.InitializeWebViewAsync(webView, (success) => {
                        isWebViewInitialized = success;
                    });
                }
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
            
            // Initialize Web API server immediately (before ROM loads)
            // API will handle NES being null and can receive commands like loading ROMs
            InitializeWebApiServer();
            
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

        private async void InitializeWebApiServer()
        {
            try
            {
                // Pass functions that return the current NES and Corruptor instances
                webApiServer = new WebApiServer(() => nes, () => corruptor);
                await webApiServer.StartAsync();
                Console.WriteLine("Web API server started successfully on http://127.0.0.1:42067");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start Web API server: {ex.Message}");
                // Don't show error to user, API is optional
            }
        }
    }
}
