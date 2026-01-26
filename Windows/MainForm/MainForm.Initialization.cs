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
            this.Text = "BrokenNes";
            Console.WriteLine("BrokenNes - Windows");
            this.ClientSize = new Size(1280, 720);
            this.MinimumSize = new Size(1280, 720);
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
            
            recentRomsMenu = new ToolStripMenuItem("Recent &Roms");
            emulatorMenu.DropDownItems.Add(recentRomsMenu);
            // Recent ROMs menu will be populated after LoadConfig() is called
            
            emulatorMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var pauseResumeItem = new ToolStripMenuItem("&Pause/Resume", null, PauseResume_Click);
            emulatorMenu.DropDownItems.Add(pauseResumeItem);
            
            var resetItem = new ToolStripMenuItem("&Reset Emulator", null, ResetEmulator_Click);
            resetItem.ShortcutKeys = Keys.Control | Keys.R;
            emulatorMenu.DropDownItems.Add(resetItem);
            
            emulatorMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var quickLoadItem = new ToolStripMenuItem("Quick Load State", null, QuickLoadState_Click);
            quickLoadItem.ShortcutKeys = Keys.F5;
            emulatorMenu.DropDownItems.Add(quickLoadItem);
            
            var quickSaveItem = new ToolStripMenuItem("Quick Save State", null, QuickSaveState_Click);
            quickSaveItem.ShortcutKeys = Keys.F7;
            emulatorMenu.DropDownItems.Add(quickSaveItem);
            
            emulatorMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var loadStateItem = new ToolStripMenuItem("Load State...", null, LoadStateFromFile_Click);
            loadStateItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.L;
            emulatorMenu.DropDownItems.Add(loadStateItem);
            
            var saveStateItem = new ToolStripMenuItem("Save State...", null, SaveStateToFile_Click);
            saveStateItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.S;
            emulatorMenu.DropDownItems.Add(saveStateItem);

            emulatorMenu.DropDownItems.Add(new ToolStripSeparator());

            var screenshotItem = new ToolStripMenuItem("Take Screenshot", null, TakeScreenshot_Click);
            screenshotItem.ShortcutKeys = Keys.F12;
            emulatorMenu.DropDownItems.Add(screenshotItem);

            var openFolderItem = new ToolStripMenuItem("Open Emulator Folder", null, OpenEmulatorFolder_Click);
            emulatorMenu.DropDownItems.Add(openFolderItem);
            
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
            displayMenu.DropDownItems.Add(hideMenuBarItem);
            
            // VSync option (DirectX only)
            if (useDirectX)
            {
                var vsyncItem = new ToolStripMenuItem("V-Sync", null, ToggleVSync_Click);
                vsyncItem.CheckOnClick = true;
                displayMenu.DropDownItems.Add(vsyncItem);
            }
            
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
            var availableNullProviders = NesEmulator.NES.GetAvailableNullProviders().ToList();
            var defaultNullProviders = new[] { "Static", "Void" };

            foreach (var providerName in defaultNullProviders)
            {
                if (!availableNullProviders.Any(p => p.Equals(providerName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var displayText = providerName.Equals("Static", StringComparison.OrdinalIgnoreCase)
                    ? "Static (Default)"
                    : "Void (Black)";

                var menuItem = new ToolStripMenuItem(displayText, null, (s, e) => SetNullProvider(providerName))
                {
                    Tag = providerName
                };
                nullProviderMenu.DropDownItems.Add(menuItem);
            }

            var otherNullProviders = availableNullProviders
                .Where(p => !defaultNullProviders.Any(d => d.Equals(p, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (otherNullProviders.Count > 0)
            {
                nullProviderMenu.DropDownItems.Add(new ToolStripSeparator());
                foreach (var providerName in otherNullProviders)
                {
                    var menuItem = new ToolStripMenuItem(providerName, null, (s, e) => SetNullProvider(providerName))
                    {
                        Tag = providerName
                    };
                    nullProviderMenu.DropDownItems.Add(menuItem);
                }
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
            
            // Emulation Speed submenu
            var emulationSpeedMenu = new ToolStripMenuItem("Emulation &Speed");
            
            var noSpeedLimitItem = new ToolStripMenuItem("No Speed Limit", null, ToggleNoSpeedLimit_Click);
            noSpeedLimitItem.CheckOnClick = true;
            emulationSpeedMenu.DropDownItems.Add(noSpeedLimitItem);
            
            var speedControlItem = new ToolStripMenuItem("Speed Control...", null, OpenSpeedControl_Click);
            emulationSpeedMenu.DropDownItems.Add(speedControlItem);
            
            configMenu.DropDownItems.Add(emulationSpeedMenu);
            
            // Emulator Behaviors submenu
            var emulatorBehaviorsMenu = new ToolStripMenuItem("Emulator &Behaviors");
            
            var bootToEmulatorItem = new ToolStripMenuItem("Boot to Emulator", null, ToggleBootToEmulator_Click);
            bootToEmulatorItem.CheckOnClick = true;
            emulatorBehaviorsMenu.DropDownItems.Add(bootToEmulatorItem);
            
            // Crash Behavior submenu (moved here)
            var crashBehaviorMenu = new ToolStripMenuItem("C&rash Behavior");
            
            var redScreenItem = new ToolStripMenuItem("Red Screen", null, (s, e) => SetCrashBehavior("RedScreen"));
            crashBehaviorMenu.DropDownItems.Add(redScreenItem);
            
            var ignoreErrorsItem = new ToolStripMenuItem("Ignore Errors", null, (s, e) => SetCrashBehavior("IgnoreErrors"));
            crashBehaviorMenu.DropDownItems.Add(ignoreErrorsItem);
            
            var imagineFixItem = new ToolStripMenuItem("Imagine Fix (Not Implemented)", null, (s, e) => { /* Disabled */ });
            imagineFixItem.Enabled = false;
            crashBehaviorMenu.DropDownItems.Add(imagineFixItem);
            
            emulatorBehaviorsMenu.DropDownItems.Add(crashBehaviorMenu);
            
            var showFpsItem = new ToolStripMenuItem("Show FPS and Input", null, ToggleShowFps_Click);
            showFpsItem.CheckOnClick = true;
            emulatorBehaviorsMenu.DropDownItems.Add(showFpsItem);
            
            configMenu.DropDownItems.Add(emulatorBehaviorsMenu);
            
            // Debug Tools submenu
            var debugToolsMenu = new ToolStripMenuItem("&Debug Tools");
            
            var startProfilingItem = new ToolStripMenuItem("Start Profiling Performance", null, ToggleProfiling_Click);
            startProfilingItem.CheckOnClick = true;
            debugToolsMenu.DropDownItems.Add(startProfilingItem);
            
            var autoScrambleItem = new ToolStripMenuItem("Auto-Scramble Cores (Testing)", null, ToggleAutoScrambleCores_Click);
            autoScrambleItem.CheckOnClick = true;
            debugToolsMenu.DropDownItems.Add(autoScrambleItem);
            
            var showConsoleItem = new ToolStripMenuItem("Show Console", null, ToggleShowConsole_Click);
            showConsoleItem.CheckOnClick = true;
            debugToolsMenu.DropDownItems.Add(showConsoleItem);
            
            var showWebmodulesMenuItem = new ToolStripMenuItem("Show Webmodules Menu", null, ToggleShowWebmodulesMenu_Click);
            showWebmodulesMenuItem.CheckOnClick = true;
            debugToolsMenu.DropDownItems.Add(showWebmodulesMenuItem);
            
            configMenu.DropDownItems.Add(debugToolsMenu);
            
            menuStrip.Items.Add(configMenu);

            // Tools & Activities menu
            var toolsMenu = new ToolStripMenuItem("&Tools && Activities");
            
            // Add webmodules that have ShowInToolsMenu flag
            var webModules = WebModuleManager.DiscoverModules();
            var toolWebModules = webModules.Where(m => m.Config.ShowInToolsMenu).ToArray();
            
            // Separate activities from tools
            var activities = toolWebModules
                .Where(m => m.Config.IsActivity)
                .OrderBy(m => m.FolderName.Equals("DeckBuilder", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var tools = toolWebModules.Where(m => !m.Config.IsActivity).ToArray();
            
            // Add activities first
            if (activities.Length > 0)
            {
                foreach (var module in activities)
                {
                    var moduleToLoad = module;
                    // Check if this module redirects to another module
                    if (!string.IsNullOrWhiteSpace(module.Config.LoadModule))
                    {
                        var targetModule = webModules.FirstOrDefault(m => 
                            m.FolderName.Equals(module.Config.LoadModule, StringComparison.OrdinalIgnoreCase));
                        if (targetModule != null)
                        {
                            moduleToLoad = targetModule;
                        }
                    }
                    var moduleItem = new ToolStripMenuItem(module.Name, null, (s, e) => LoadWebModule(moduleToLoad));
                    toolsMenu.DropDownItems.Add(moduleItem);
                }
            }
            
            // Add separator between activities and tools
            if (activities.Length > 0 && tools.Length > 0)
            {
                toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            }
            
            // Add tools
            if (tools.Length > 0)
            {
                foreach (var module in tools)
                {
                    var moduleToLoad = module;
                    // Check if this module redirects to another module
                    if (!string.IsNullOrWhiteSpace(module.Config.LoadModule))
                    {
                        var targetModule = webModules.FirstOrDefault(m => 
                            m.FolderName.Equals(module.Config.LoadModule, StringComparison.OrdinalIgnoreCase));
                        if (targetModule != null)
                        {
                            moduleToLoad = targetModule;
                        }
                    }
                    var moduleItem = new ToolStripMenuItem(module.Name, null, (s, e) => LoadWebModule(moduleToLoad));
                    toolsMenu.DropDownItems.Add(moduleItem);
                }
            }
            
            menuStrip.Items.Add(toolsMenu);
            
            // Handle menu opening to dynamically add exit option for current tool/activity
            toolsMenu.DropDownOpening += (s, e) =>
            {
                // Remove any existing exit items first
                var itemsToRemove = toolsMenu.DropDownItems.OfType<ToolStripItem>()
                    .Where(item => item.Tag?.ToString() == "ExitModule")
                    .ToList();
                foreach (var item in itemsToRemove)
                {
                    toolsMenu.DropDownItems.Remove(item);
                }
                
                // Add exit item if we have a current tool/activity loaded
                if (currentToolOrActivityModule != null)
                {
                    var exitSeparator = new ToolStripSeparator { Tag = "ExitModule" };
                    toolsMenu.DropDownItems.Add(exitSeparator);
                    
                    var exitItem = new ToolStripMenuItem(
                        $"Exit {currentToolOrActivityModule.Name}",
                        null,
                        (sender, args) => SwitchViewMode(ViewMode.Emulator))
                    {
                        Font = new Font(toolsMenu.Font, FontStyle.Bold),
                        Tag = "ExitModule"
                    };
                    toolsMenu.DropDownItems.Add(exitItem);
                }
            };
            
            // Webmodules menu for view modes
            webModulesMenu = new ToolStripMenuItem("&Webmodules");
            
            var emulatorModeItem = new ToolStripMenuItem("Emulator Mode", null, (s, e) => SwitchViewMode(ViewMode.Emulator));
            emulatorModeItem.ShortcutKeys = Keys.Control | Keys.D1;
            webModulesMenu.DropDownItems.Add(emulatorModeItem);
            
            var widgetModeItem = new ToolStripMenuItem("Widget Mode", null, (s, e) => SwitchViewMode(ViewMode.Widget));
            widgetModeItem.ShortcutKeys = Keys.Control | Keys.D2;
            webModulesMenu.DropDownItems.Add(widgetModeItem);
            
            var webModeItem = new ToolStripMenuItem("Web Mode (Test Page)", null, (s, e) => {
                // Load the webmodules index page
                string webmodulesIndexUri = $"https://{WebModuleManager.SharedVirtualHostName}/index.html";
                Helpers.WebViewHelper.NavigateToUri(webView, webmodulesIndexUri);
                SwitchViewMode(ViewMode.Web);
            });
            webModeItem.ShortcutKeys = Keys.Control | Keys.D3;
            webModulesMenu.DropDownItems.Add(webModeItem);
            
            var overlayModeItem = new ToolStripMenuItem("Overlay Mode", null, (s, e) => SwitchViewMode(ViewMode.Overlay));
            overlayModeItem.ShortcutKeys = Keys.Control | Keys.D4;
            webModulesMenu.DropDownItems.Add(overlayModeItem);
            
            // Add separator before webmodules
            webModulesMenu.DropDownItems.Add(new ToolStripSeparator());
            
            // Add all web modules (webModules already discovered above for Tools menu)
            if (webModules.Length > 0)
            {
                foreach (var module in webModules)
                {
                    var moduleItem = new ToolStripMenuItem(module.Name, null, (s, e) => LoadWebModule(module));
                    webModulesMenu.DropDownItems.Add(moduleItem);
                }
            }
            else
            {
                var noModulesItem = new ToolStripMenuItem("(No modules available)")
                {
                    Enabled = false
                };
                webModulesMenu.DropDownItems.Add(noModulesItem);
            }
            
            menuStrip.Items.Add(webModulesMenu);
            
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
                    
                    // Initialize WebView2 asynchronously on UI thread (don't await to avoid blocking constructor)
                    _ = InitializeWebView2Async();
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
            
            // Initialize high-level audio engine for music and SFX
            try
            {
                audioEngine = new AudioEngine();
                Console.WriteLine("AudioEngine initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize audio engine: {ex.Message}");
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
            _ = EnsureWebApiServerRunningAsync();
            
            // Load the default embedded ROM (but don't load Home yet - wait for WebView2)
            LoadEmbeddedRom(allowHomeWebModule: false);
            
            // If we need to load Home, do it after WebView2 is ready
            if (!config.BootToEmulator)
            {
                _ = LoadHomeWhenReadyAsync();
            }
        }
        
        private async Task LoadHomeWhenReadyAsync()
        {
            // Wait for WebView2 to be ready (up to 5 seconds)
            Console.WriteLine("[LoadHomeWhenReady] Waiting for WebView2 to initialize...");
            for (int i = 0; i < 50; i++) // 50 * 100ms = 5 seconds max
            {
                if (isWebViewInitialized) break;
                await Task.Delay(100);
            }
            
            if (isWebViewInitialized)
            {
                Console.WriteLine("[LoadHomeWhenReady] WebView2 ready, loading Home...");
                LoadHomeWebModule();
            }
            else
            {
                Console.WriteLine("[LoadHomeWhenReady] WebView2 failed to initialize in time");
                MessageBox.Show("WebView2 initialization took too long. You can manually switch to web modes from the menu.", 
                    "Initialization Timeout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
            
            // Initialize webmodule input manager (X/Y buttons)
            // Use Player 1 config for webmodule buttons
            if (webModuleInputManager == null)
            {
                webModuleInputManager = new WebModuleInputManager();
            }
            
            webModuleInputManager.SetPlayerConfig(p1Config);
            Console.WriteLine($"Webmodule input (X/Y) configured:");
            Console.WriteLine($"  X: {p1Config.X.DisplayName}");
            Console.WriteLine($"  Y: {p1Config.Y.DisplayName}");
            
            // Wire up button events to WebAPI
            webModuleInputManager.OnButtonPressed += (button) =>
            {
                webApiServer?.NotifyButtonPressed(button);
            };
            
            webModuleInputManager.OnButtonReleased += (button) =>
            {
                webApiServer?.NotifyButtonReleased(button);
            };
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

        private async Task EnsureWebApiServerRunningAsync()
        {
            await webApiServerLock.WaitAsync();
            try
            {
                if (webApiServer == null)
                {
                    // Pass functions that return the current NES and Corruptor instances
                    // Also pass webView and SwitchViewMode for navigation support
                    webApiServer = new WebApiServer(
                        () => nes,
                        () => corruptor,
                        () => imagineEngine,
                        SetCrashBehavior,
                        () => webView,
                        (mode, skipNav) => SwitchViewMode(mode, skipNavigation: skipNav),
                        this,  // Pass the main form as UI control for thread marshalling
                        CloseAllMenus,  // Pass the close menus handler
                        ToggleFullscreen,  // Pass the fullscreen toggle handler
                        () => audioEngine,  // Pass the audio engine
                        (filename, preserveShader) => LoadBuiltInRomAsync(filename, preserveShader),  // Pass the ROM loader
                        ResumeEmulationIfPaused,  // Pass the resume emulation handler
                        HideContinueButton,  // Pass the continue button hide handler
                        () => achievementsEngine,  // Pass the achievements engine
                        engine => achievementsEngine = engine,  // Allow WebAPI to initialize achievements
                        HideMenu,  // Pass the hide menu handler
                        ShowMenu,  // Pass the show menu handler
                        ResetGame,  // Pass the reset game handler
                        () => BrokenNes.Windows.Rendering.NesDirectXRenderer.GetAvailableBackgrounds(),  // Available backgrounds
                        SetBackground,  // Set background handler
                        () => NesEmulator.NES.GetAvailableNullProviders(),  // Available null providers
                        SetNullProvider,  // Set null provider handler
                        CloseRomFromApi,  // Close ROM handler
                        () => nes?.RomPath,  // Current ROM path
                        () => nes?.RomName,  // Current ROM name
                        LoadRomFromApi  // Load ROM by path
                    );
                }

                if (!webApiServer.IsRunning)
                {
                    await webApiServer.StartAsync();
                    Console.WriteLine("Web API server started successfully on http://127.0.0.1:42067");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start Web API server: {ex.Message}");
                // Don't show error to user, API is optional
            }
            finally
            {
                webApiServerLock.Release();
            }
        }
        
        private async Task InitializeWebView2Async()
        {
            try
            {
                isWebViewInitialized = await Helpers.WebViewHelper.InitializeWebViewAsync(webView);
                if (isWebViewInitialized)
                {
                    Console.WriteLine("[MainForm] WebView2 is now ready for use");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainForm] WebView2 initialization failed: {ex.Message}");
                isWebViewInitialized = false;
            }
        }
    }
}
