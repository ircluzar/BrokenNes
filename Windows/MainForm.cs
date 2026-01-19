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
        private System.Windows.Forms.Timer? renderTimer;
        private NesDirectXRenderer dxRenderer;
        private Panel displayPanel;
        private DirectBitmap? frameBuffer;
        private DirectBitmap? backBuffer; // Double buffering
        private AudioManager? audioManager;
        private string currentRomPath = string.Empty;
        private EmulatorConfig config = new EmulatorConfig();
        private bool useDirectX = true;
        
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
                InitializeEmulator();
                Console.WriteLine("InitializeEmulator completed");
                SetupKeyMapping();
                Console.WriteLine("SetupKeyMapping completed");
                LoadConfig();
                Console.WriteLine("LoadConfig completed");
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
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.KeyPreview = true;
            
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
            
            // Create render timer for UI updates (60 FPS)
            renderTimer = new System.Windows.Forms.Timer();
            renderTimer.Interval = 16; // ~60 FPS
            renderTimer.Tick += RenderTimer_Tick;
            
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
            
            // Load the default embedded ROM
            LoadEmbeddedRom();
        }
        
        private void SetupKeyMapping()
        {
            // Default NES controller mapping
            // A, B, Select, Start, Up, Down, Left, Right
            keyMap[Keys.Z] = 0; // A
            keyMap[Keys.X] = 1; // B
            keyMap[Keys.A] = 2; // Select
            keyMap[Keys.S] = 3; // Start
            keyMap[Keys.Up] = 4; // Up
            keyMap[Keys.Down] = 5; // Down
            keyMap[Keys.Left] = 6; // Left
            keyMap[Keys.Right] = 7; // Right
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
            renderTimer?.Start();
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
            
            renderTimer?.Stop();
            audioManager?.Stop();
        }
        
        private void EmulationThreadProc()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            const double targetFrameTime = 1.0 / 60.0; // 60 FPS
            double accumulator = 0;
            
            while (isEmulationRunning)
            {
                if (isPaused)
                {
                    Thread.Sleep(10);
                    stopwatch.Restart();
                    accumulator = 0;
                    continue;
                }
                
                double deltaTime = stopwatch.Elapsed.TotalSeconds;
                stopwatch.Restart();
                accumulator += deltaTime;
                
                // Run emulation frames to catch up
                while (accumulator >= targetFrameTime && isEmulationRunning && !isPaused)
                {
                    lock (emulationLock)
                    {
                        if (nes != null)
                        {
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
                                
                                // Get the framebuffer from the PPU and copy to backbuffer
                                byte[]? nesFrameBuffer = nes.GetFrameBuffer();
                                if (nesFrameBuffer != null && nesFrameBuffer.Length == NES_WIDTH * NES_HEIGHT * 4 && backBuffer != null)
                                {
                                    backBuffer.CopyFromBytes(nesFrameBuffer);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Emulation error: {ex.Message}");
                                isEmulationRunning = false;
                            }
                        }
                    }
                    
                    accumulator -= targetFrameTime;
                }
                
                // Small sleep to prevent CPU spinning
                if (accumulator < targetFrameTime)
                {
                    int sleepTime = (int)((targetFrameTime - accumulator) * 1000 * 0.8);
                    if (sleepTime > 0)
                    {
                        Thread.Sleep(sleepTime);
                    }
                }
            }
        }
        
        private void RenderTimer_Tick(object? sender, EventArgs e)
        {
            if (backBuffer == null || frameBuffer == null) return;
            
            try
            {
                // Swap buffers (copy backbuffer to frontbuffer)
                lock (emulationLock)
                {
                    Array.Copy(backBuffer.Bits, frameBuffer.Bits, backBuffer.Bits.Length);
                }
                
                // Render using DirectX if available
                if (useDirectX && dxRenderer?.IsReady == true)
                {
                    dxRenderer.DrawFrame(frameBuffer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Render error: {ex.Message}");
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
                MessageBox.Show($"Emulation error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void MainForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                PauseResume_Click(sender, e);
                e.Handled = true;
                return;
            }

            lock (emulationLock)
            {
                if (nes != null && keyMap.TryGetValue(e.KeyCode, out int buttonIndex))
                {
                    nes.SetButton(0, buttonIndex, true);
                    e.Handled = true;
                }
            }
        }
        
        private void MainForm_KeyUp(object? sender, KeyEventArgs e)
        {
            lock (emulationLock)
            {
                if (nes != null && keyMap.TryGetValue(e.KeyCode, out int buttonIndex))
                {
                    nes.SetButton(0, buttonIndex, false);
                    e.Handled = true;
                }
            }
        }
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopEmulation();
            renderTimer?.Dispose();
            audioManager?.Dispose();
            frameBuffer?.Dispose();
            backBuffer?.Dispose();
            dxRenderer?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
