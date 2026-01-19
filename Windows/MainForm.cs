using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NesEmulator;
using NesEmulator.Shaders;
using BrokenNes.Windows.Rendering;

namespace BrokenNes.Windows
{
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
        
        // FPS tracking for audio speed adjustment
        private double currentFps = 60.0;
        private int fpsFrameCount = 0;
        private System.Diagnostics.Stopwatch? fpsStopwatch;
        
        // Speed control
        private SpeedControlForm? speedControlForm;
        private volatile float speedOverride = 1.0f;
        private volatile bool hasSpeedOverride = false;
        
        // Auto-scramble cores testing
        private System.Windows.Forms.Timer? autoScrambleTimer;
        private Random scrambleRandom = new Random();
        
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
            
            // Image submenu
            var imageMenu = new ToolStripMenuItem("&Image");
            
            var pixelPerfectItem = new ToolStripMenuItem("Force Pixel Perfect", null, TogglePixelPerfect_Click);
            pixelPerfectItem.CheckOnClick = true;
            imageMenu.DropDownItems.Add(pixelPerfectItem);
            
            var nativeAspectItem = new ToolStripMenuItem("Force Native Aspect Ratio", null, ToggleNativeAspect_Click);
            nativeAspectItem.CheckOnClick = true;
            imageMenu.DropDownItems.Add(nativeAspectItem);
            
            var nearestNeighborItem = new ToolStripMenuItem("Scaling Nearest Neighbor", null, ToggleNearestNeighbor_Click);
            nearestNeighborItem.CheckOnClick = true;
            imageMenu.DropDownItems.Add(nearestNeighborItem);
            
            imageMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var zoom1xItem = new ToolStripMenuItem("Resize Zoom 1x", null, (s, e) => SetWindowZoom(1));
            imageMenu.DropDownItems.Add(zoom1xItem);
            
            var zoom2xItem = new ToolStripMenuItem("Resize Zoom 2x", null, (s, e) => SetWindowZoom(2));
            imageMenu.DropDownItems.Add(zoom2xItem);
            
            var zoom4xItem = new ToolStripMenuItem("Resize Zoom 4x", null, (s, e) => SetWindowZoom(4));
            imageMenu.DropDownItems.Add(zoom4xItem);
            
            configMenu.DropDownItems.Add(imageMenu);
            
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
            
            var player1Menu = new ToolStripMenuItem("Player 1");
            
            var bindAItem = new ToolStripMenuItem("A Button...", null, (s, e) => BindControllerKey("A", config.P1KeyA, k => config.P1KeyA = k));
            player1Menu.DropDownItems.Add(bindAItem);
            
            var bindBItem = new ToolStripMenuItem("B Button...", null, (s, e) => BindControllerKey("B", config.P1KeyB, k => config.P1KeyB = k));
            player1Menu.DropDownItems.Add(bindBItem);
            
            var bindSelectItem = new ToolStripMenuItem("Select Button...", null, (s, e) => BindControllerKey("Select", config.P1KeySelect, k => config.P1KeySelect = k));
            player1Menu.DropDownItems.Add(bindSelectItem);
            
            var bindStartItem = new ToolStripMenuItem("Start Button...", null, (s, e) => BindControllerKey("Start", config.P1KeyStart, k => config.P1KeyStart = k));
            player1Menu.DropDownItems.Add(bindStartItem);
            
            player1Menu.DropDownItems.Add(new ToolStripSeparator());
            
            var bindUpItem = new ToolStripMenuItem("D-Pad Up...", null, (s, e) => BindControllerKey("Up", config.P1KeyUp, k => config.P1KeyUp = k));
            player1Menu.DropDownItems.Add(bindUpItem);
            
            var bindDownItem = new ToolStripMenuItem("D-Pad Down...", null, (s, e) => BindControllerKey("Down", config.P1KeyDown, k => config.P1KeyDown = k));
            player1Menu.DropDownItems.Add(bindDownItem);
            
            var bindLeftItem = new ToolStripMenuItem("D-Pad Left...", null, (s, e) => BindControllerKey("Left", config.P1KeyLeft, k => config.P1KeyLeft = k));
            player1Menu.DropDownItems.Add(bindLeftItem);
            
            var bindRightItem = new ToolStripMenuItem("D-Pad Right...", null, (s, e) => BindControllerKey("Right", config.P1KeyRight, k => config.P1KeyRight = k));
            player1Menu.DropDownItems.Add(bindRightItem);
            
            controllersMenu.DropDownItems.Add(player1Menu);
            
            configMenu.DropDownItems.Add(controllersMenu);
            
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
            
            var showFpsItem = new ToolStripMenuItem("Show FPS", null, ToggleShowFps_Click);
            showFpsItem.CheckOnClick = true;
            configMenu.DropDownItems.Add(showFpsItem);
            
            var startProfilingItem = new ToolStripMenuItem("Start Profiling Performance", null, ToggleProfiling_Click);
            startProfilingItem.CheckOnClick = true;
            configMenu.DropDownItems.Add(startProfilingItem);
            
            configMenu.DropDownItems.Add(new ToolStripSeparator());
            
            var autoScrambleItem = new ToolStripMenuItem("Auto-Scramble Cores (Testing)", null, ToggleAutoScrambleCores_Click);
            autoScrambleItem.CheckOnClick = true;
            configMenu.DropDownItems.Add(autoScrambleItem);
            
            menuStrip.Items.Add(configMenu);
            
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
            
            // Create display panel
            displayPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            this.Controls.Add(displayPanel);
            
            // Create DirectX renderer for hardware-accelerated rendering
            try
            {
                dxRenderer = new NesDirectXRenderer
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black
                };
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
                try
                {
                    dxRenderer.Initialize(NES_WIDTH, NES_HEIGHT);
                    NesShaderControl.Initialize(dxRenderer);
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
            
            // Load key bindings from config
            if (Enum.TryParse<Keys>(config.P1KeyA, out Keys keyA))
            {
                keyMap[keyA] = 0; // A
                Console.WriteLine($"  A (button 0): {keyA}");
            }
            if (Enum.TryParse<Keys>(config.P1KeyB, out Keys keyB))
            {
                keyMap[keyB] = 1; // B
                Console.WriteLine($"  B (button 1): {keyB}");
            }
            if (Enum.TryParse<Keys>(config.P1KeySelect, out Keys keySelect))
            {
                keyMap[keySelect] = 2; // Select
                Console.WriteLine($"  Select (button 2): {keySelect}");
            }
            if (Enum.TryParse<Keys>(config.P1KeyStart, out Keys keyStart))
            {
                keyMap[keyStart] = 3; // Start
                Console.WriteLine($"  Start (button 3): {keyStart}");
            }
            if (Enum.TryParse<Keys>(config.P1KeyUp, out Keys keyUp))
            {
                keyMap[keyUp] = 4; // Up
                Console.WriteLine($"  Up (button 4): {keyUp}");
            }
            if (Enum.TryParse<Keys>(config.P1KeyDown, out Keys keyDown))
            {
                keyMap[keyDown] = 5; // Down
                Console.WriteLine($"  Down (button 5): {keyDown}");
            }
            if (Enum.TryParse<Keys>(config.P1KeyLeft, out Keys keyLeft))
            {
                keyMap[keyLeft] = 6; // Left
                Console.WriteLine($"  Left (button 6): {keyLeft}");
            }
            if (Enum.TryParse<Keys>(config.P1KeyRight, out Keys keyRight))
            {
                keyMap[keyRight] = 7; // Right
                Console.WriteLine($"  Right (button 7): {keyRight}");
            }
            
            // Initialize input manager with the key map
            inputManager = new InputManager();
            inputManager.SetKeyMap(keyMap);
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
            
            // Update Image submenu checkmarks
            var imageMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "&Image");
            
            if (imageMenu != null)
            {
                foreach (var item in imageMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (item.Text.Contains("Pixel Perfect"))
                        item.Checked = config.ForcePixelPerfect;
                    else if (item.Text.Contains("Native Aspect"))
                        item.Checked = config.ForceNativeAspectRatio;
                    else if (item.Text.Contains("Nearest Neighbor"))
                        item.Checked = config.ScalingNearestNeighbor;
                    // Zoom options are not checkboxes, so we don't update them
                }
            }
            
            // Update emulation options checkmarks
            foreach (var item in configMenu.DropDownItems.OfType<ToolStripMenuItem>())
            {
                if (item.Text.Contains("No Speed Limit"))
                    item.Checked = config.NoSpeedLimit;
                else if (item.Text.Contains("Show FPS"))
                    item.Checked = config.ShowFps;
                else if (item.Text.Contains("Start Profiling Performance"))
                    item.Checked = config.ProfilingEnabled;
                else if (item.Text.Contains("Auto-Scramble Cores"))
                    item.Checked = config.AutoScrambleCores;
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
                }
                
                currentRomPath = path;
                this.Text = $"BrokenNes - {Path.GetFileName(path)}";
                
                // Add to recent ROMs
                config.AddRecentRom(path);
                
                // Apply saved core selections
                ApplySavedCoreSelections();
                
                // Apply crash behavior
                ApplyCrashBehavior();
                
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
            StopEmulation();
            
            lock (emulationLock)
            {
                nes = null;
            }
            
            currentRomPath = string.Empty;
            this.Text = "BrokenNes - Windows";
            
            // Clear the display
            if (frameBuffer != null)
            {
                frameBuffer.Clear(unchecked((int)0xFF000000)); // Black
                
                if (useDirectX && dxRenderer?.IsReady == true)
                {
                    dxRenderer.DrawFrame(frameBuffer);
                }
            }
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
            if (nes == null)
            {
                MessageBox.Show("Please load a ROM first.", "No ROM Loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            using var openDialog = new OpenFileDialog
            {
                Filter = "State Files (*.state)|*.state|All Files (*.*)|*.*",
                Title = "Load Save State",
                DefaultExt = "state"
            };
            
            if (openDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    string stateJson = File.ReadAllText(openDialog.FileName);
                    
                    // Pause emulation during state load
                    bool wasPaused = isPaused;
                    isPaused = true;
                    
                    lock (emulationLock)
                    {
                        nes.LoadState(stateJson);
                    }
                    
                    isPaused = wasPaused;
                    
                    this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)} [State Loaded]";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load state:\n{ex.Message}", "Load State Error", 
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
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
                Filter = "State Files (*.state)|*.state|All Files (*.*)|*.*",
                Title = "Save Save State",
                DefaultExt = "state",
                FileName = Path.GetFileNameWithoutExtension(currentRomPath) + ".state"
            };
            
            if (saveDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    // Pause emulation during state save
                    bool wasPaused = isPaused;
                    isPaused = true;
                    
                    string stateJson;
                    lock (emulationLock)
                    {
                        stateJson = nes.SaveState();
                    }
                    
                    isPaused = wasPaused;
                    
                    File.WriteAllText(saveDialog.FileName, stateJson);
                    
                    this.Text = $"BrokenNes - {Path.GetFileName(currentRomPath)} [State Saved]";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save state:\n{ex.Message}", "Save State Error", 
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
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
                
                isPaused = wasPaused;
                
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
        private void BindControllerKey(string buttonName, string currentKey, Action<string> setKey)
        {
            var dialog = new Form
            {
                Text = $"Bind {buttonName}",
                Size = new Size(350, 150),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                KeyPreview = true
            };
            
            var label = new Label
            {
                Text = $"Press any key to bind to {buttonName}\n\nCurrent: {currentKey}",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Font = new Font(Font.FontFamily, 10)
            };
            
            dialog.Controls.Add(label);
            
            dialog.KeyDown += (s, e) =>
            {
                string keyName = e.KeyCode.ToString();
                setKey(keyName);
                config.Save();
                SetupKeyMapping();
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };
            
            dialog.ShowDialog(this);
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
                        audioManager?.SetSpeedMultiplier(speedOverride);
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
                speedControlForm.SpeedChanged += SpeedControlForm_SpeedChanged;
                speedControlForm.FormClosed += (s, args) =>
                {
                    hasSpeedOverride = false;
                    speedOverride = 1.0f;
                    
                    // Reset audio speed and clear buffer to prevent desync
                    audioManager?.SetSpeedMultiplier(1.0f);
                    audioManager?.ClearBuffer();
                };
            }
            
            hasSpeedOverride = true;
            speedControlForm.Show(this);
            speedControlForm.Focus();
        }
        
        private void SpeedControlForm_SpeedChanged(object? sender, float speed)
        {
            speedOverride = speed;
            hasSpeedOverride = true;
            
            // Update audio manager immediately for responsive speed changes
            audioManager?.SetSpeedMultiplier(speed);
        }
        
        private void ToggleShowFps_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ShowFps = menuItem.Checked;
                config.Save();
                ApplyImageSettings();
                UpdateConfigMenus();
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
        
        private void SetCrashBehavior(string behavior)
        {
            config.CrashBehavior = behavior;
            config.Save();
            
            // Apply to current NES instance if one is running
            ApplyCrashBehavior();
            
            UpdateConfigMenus();
            
            Console.WriteLine($"Crash behavior set to: {behavior}");
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
                                break;
                            default: // "RedScreen"
                                nes.SetCrashBehavior(NES.CrashBehavior.RedScreen);
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error applying crash behavior: {ex.Message}");
            }
        }
        
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
                dxRenderer.PixelPerfect = config.ForcePixelPerfect;
                
                // Apply interpolation mode based on ScalingNearestNeighbor
                dxRenderer.InterpolationMode = config.ScalingNearestNeighbor 
                    ? SharpDX.Direct2D1.BitmapInterpolationMode.NearestNeighbor
                    : SharpDX.Direct2D1.BitmapInterpolationMode.Linear;
                
                // Apply aspect ratio setting
                dxRenderer.ForceNativeAspectRatio = config.ForceNativeAspectRatio;
                
                // Apply FPS display setting
                dxRenderer.ShowFps = config.ShowFps;
                
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
                            var apuProperty = bus.GetType().GetProperty("Apu");
                            if (apuProperty != null)
                            {
                                var apu = apuProperty.GetValue(bus) as IAPU;
                                apu?.SetEnabledChannels(config.EnabledChannels);
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
                            // Poll input manager for controller and update NES button states
                            if (inputManager != null)
                            {
                                using (PerformanceProfiler.Time("Input.Poll"))
                                {
                                    inputManager.Poll();
                                    for (int i = 0; i < 8; i++)
                                    {
                                        nes.SetButton(0, i, inputManager.GetButton(i));
                                    }
                                }
                            }
                            
                            // Enable static for test.nes ROM (like in web version)
                            bool isTestRom = string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase);
                            nes.EnableStatic(isTestRom);
                            
                            // Run one frame of emulation
                            using (PerformanceProfiler.Time("NES.RunFrame"))
                            {
                                nes.RunFrame();
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
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
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
