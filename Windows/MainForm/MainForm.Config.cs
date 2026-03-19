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
using PngPayloadEmbedding;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        private void LoadConfig()
        {
            try
            {
                config = EmulatorConfig.Load();
                useDirectX = config.UseDirectX;
                EnsureUnlockedProgressionCapabilities();
                
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
            
            // Update Webmodules menu visibility
            if (webModulesMenu != null)
            {
                webModulesMenu.Visible = config.ShowWebmodulesMenu;
            }
            
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
                    else if (item.Text.Contains("V-Sync"))
                        item.Checked = config.EnableVSync;
                    // Zoom options are not checkboxes, so we don't update them
                }
            }
            
            // Update emulation options checkmarks (now in various submenus)
            
            // Update Emulation Speed submenu
            var emulationSpeedMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "Emulation &Speed");
            
            if (emulationSpeedMenu != null)
            {
                foreach (var item in emulationSpeedMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (item.Text.Contains("No Speed Limit"))
                        item.Checked = config.NoSpeedLimit;
                }
            }
            
            // Update Emulator Behaviors submenu
            var emulatorBehaviorsMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "Emulator &Behaviors");
            
            if (emulatorBehaviorsMenu != null)
            {
                foreach (var item in emulatorBehaviorsMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (item.Text.Contains("Boot to Emulator"))
                        item.Checked = config.BootToEmulator;
                    else if (item.Text.Contains("Show FPS and Input"))
                        item.Checked = config.ShowFps;
                }
            }
            
            // Update Debug Tools submenu
            var debugToolsMenu = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "&Debug Tools");
            
            if (debugToolsMenu != null)
            {
                foreach (var item in debugToolsMenu.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (item.Text.Contains("Start Profiling Performance"))
                        item.Checked = config.ProfilingEnabled;
                    else if (item.Text.Contains("Auto-Scramble Cores"))
                        item.Checked = config.AutoScrambleCores;
                    else if (item.Text.Contains("Show Console"))
                        item.Checked = config.ShowConsole;
                    else if (item.Text.Contains("Show Webmodules Menu"))
                        item.Checked = config.ShowWebmodulesMenu;
                }
            }
            
            // Update Crash Behavior submenu checkmarks (nested inside Emulator Behaviors menu)
            var emulatorBehaviorsMenuForCrash = configMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "Emulator &Behaviors");
            
            if (emulatorBehaviorsMenuForCrash != null)
            {
                var crashBehaviorMenu = emulatorBehaviorsMenuForCrash.DropDownItems.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(m => m.Text == "C&rash Behavior");
                
                if (crashBehaviorMenu != null)
                {
                    var progressionSave = LoadProgressionSnapshot();
                    var imagineUnlocked = IsImagineBugUnlocked(progressionSave);
                    foreach (var item in crashBehaviorMenu.DropDownItems.OfType<ToolStripMenuItem>())
                    {
                        var behavior = item.Tag as string;
                        if (string.Equals(behavior, "RedScreen", StringComparison.OrdinalIgnoreCase))
                            item.Checked = (config.CrashBehavior == "RedScreen");
                        else if (string.Equals(behavior, "IgnoreErrors", StringComparison.OrdinalIgnoreCase))
                            item.Checked = (config.CrashBehavior == "IgnoreErrors");
                        else if (string.Equals(behavior, "ImagineFix", StringComparison.OrdinalIgnoreCase))
                        {
                            item.Checked = (config.CrashBehavior == "ImagineFix");
                            item.Enabled = imagineUnlocked;
                            item.Text = imagineUnlocked ? "Imagine Fix" : "Imagine Fix [Locked]";
                        }
                    }
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
                    var backgroundName = item.Tag as string ?? item.Text;
                    item.Checked = backgroundName.Equals(config.SelectedBackground, StringComparison.OrdinalIgnoreCase);
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
                    var providerName = item.Tag as string ?? item.Text;
                    item.Checked = providerName.Equals(config.SelectedNullProvider, StringComparison.OrdinalIgnoreCase);
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
            var progressionSave = LoadProgressionSnapshot();
            EnsureUnlockedProgressionSelections(progressionSave);
            
            // SHADER
            shaderMenu.DropDownItems.Clear();
            
            // Add shader enable/disable toggle
            var toggleShaderItem = new ToolStripMenuItem("Enable Shaders", null, (s, e) => {
                if (useDirectX && dxRenderer != null)
                {
                    dxRenderer.UseShader = !dxRenderer.UseShader;
                    ((ToolStripMenuItem)s).Checked = dxRenderer.UseShader;
                    Helpers.ConfigHelper.Update(config, c => c.ShadersEnabled = dxRenderer.UseShader);
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
                    var unlocked = IsShaderUnlocked(shaderName, progressionSave);
                    var shaderInfo = NesShaderControl.GetShaderInfo(
                        Enum.Parse<NesShaderManager.ShaderType>(shaderName));
                    var item = new ToolStripMenuItem(shaderInfo.DisplayName, null, (s, e) => {
                        NesShaderControl.SwitchShader(shaderName);
                        Helpers.ConfigHelper.Update(config, c => c.CurrentShader = shaderName);
                        UpdateCoresMenus(); // Refresh to update checkmarks
                    });
                    item.ToolTipText = shaderInfo.Description;
                    item.Enabled = unlocked;
                    if (!unlocked)
                    {
                        item.Text = $"{shaderInfo.DisplayName} [Locked]";
                    }
                    item.Checked = (shaderName == config.CurrentShader);
                    
                    // Add hover event to request overlay display
                    string capturedShaderName = shaderName; // Capture for closure
                    item.MouseEnter += (s, e) => RequestOverlayDisplayCard("shader", capturedShaderName);
                    
                    shaderMenu.DropDownItems.Add(item);
                }
                
                shaderMenu.DropDownItems.Add(new ToolStripSeparator());
                
                // Shader strength control
                var strengthMenu = new ToolStripMenuItem("Shader Strength");
                foreach (var strength in new[] { 0.5f, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f })
                {
                    var strengthItem = new ToolStripMenuItem($"{strength:F1}x", null, (s, e) => {
                        NesShaderControl.SetShaderStrength(strength);
                        Helpers.ConfigHelper.Update(config, c => c.ShaderStrength = strength);
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
            
            // Add event to clear overlay when shader menu closes
            shaderMenu.DropDownClosed += (s, e) => RequestOverlayClearCard();

            // APU - single select with checkmarks
            apuMenu.DropDownItems.Clear();
            string currentApuCore = config.SelectedApuCore;
            foreach (var coreId in CoreRegistry.ApuIds)
            {
                var unlocked = IsApuCoreUnlocked(coreId, progressionSave);
                var item = new ToolStripMenuItem(coreId, null, (s, e) => SetApuCore(coreId));
                item.Enabled = unlocked;
                if (!unlocked)
                {
                    item.Text = $"{coreId} [Locked]";
                }
                item.Checked = (coreId == currentApuCore);
                
                // Add hover event to request overlay display
                item.MouseEnter += (s, e) => RequestOverlayDisplayCard("apu", coreId);
                
                apuMenu.DropDownItems.Add(item);
            }
            
            // Add event to clear overlay when APU menu closes
            apuMenu.DropDownClosed += (s, e) => RequestOverlayClearCard();

            // CPU - single select with checkmarks
            cpuMenu.DropDownItems.Clear();
            string currentCpuCore = config.SelectedCpuCore;
            foreach (var coreId in CoreRegistry.CpuIds)
            {
                var unlocked = IsCpuCoreUnlocked(coreId, progressionSave);
                var item = new ToolStripMenuItem(coreId, null, (s, e) => SetCpuCore(coreId));
                item.Enabled = unlocked;
                if (!unlocked)
                {
                    item.Text = $"{coreId} [Locked]";
                }
                item.Checked = (coreId == currentCpuCore);
                
                // Add hover event to request overlay display
                item.MouseEnter += (s, e) => RequestOverlayDisplayCard("cpu", coreId);
                
                cpuMenu.DropDownItems.Add(item);
            }
            
            // Add event to clear overlay when CPU menu closes
            cpuMenu.DropDownClosed += (s, e) => RequestOverlayClearCard();

            // PPU - single select with checkmarks
            ppuMenu.DropDownItems.Clear();
            string currentPpuCore = config.SelectedPpuCore;
            foreach (var coreId in CoreRegistry.PpuIds)
            {
                var unlocked = IsPpuCoreUnlocked(coreId, progressionSave);
                var item = new ToolStripMenuItem(coreId, null, (s, e) => SetPpuCore(coreId));
                item.Enabled = unlocked;
                if (!unlocked)
                {
                    item.Text = $"{coreId} [Locked]";
                }
                item.Checked = (coreId == currentPpuCore);
                
                // Add hover event to request overlay display
                item.MouseEnter += (s, e) => RequestOverlayDisplayCard("ppu", coreId);
                
                ppuMenu.DropDownItems.Add(item);
            }
            
            // Add event to clear overlay when PPU menu closes
            ppuMenu.DropDownClosed += (s, e) => RequestOverlayClearCard();
        }
        
        private void ApplySavedCoreSelections()
        {
            if (nes == null) return;
            var progressionSave = LoadProgressionSnapshot();
            EnsureUnlockedProgressionSelections(progressionSave);
            
            // Apply CPU core (default to FMC if not valid)
            if (!string.IsNullOrEmpty(config.SelectedCpuCore)
                && CoreRegistry.CpuIds.Contains(config.SelectedCpuCore)
                && IsCpuCoreUnlocked(config.SelectedCpuCore, progressionSave))
            {
                nes.SetCpuCore(config.SelectedCpuCore);
            }
            else
            {
                Helpers.ConfigHelper.Update(config, c => c.SelectedCpuCore = "FMC");
                nes.SetCpuCore("FMC");
            }
            
            // Apply PPU core (default to FMC if not valid)
            if (!string.IsNullOrEmpty(config.SelectedPpuCore)
                && CoreRegistry.PpuIds.Contains(config.SelectedPpuCore)
                && IsPpuCoreUnlocked(config.SelectedPpuCore, progressionSave))
            {
                nes.SetPpuCore(config.SelectedPpuCore);
            }
            else
            {
                Helpers.ConfigHelper.Update(config, c => c.SelectedPpuCore = "FMC");
                nes.SetPpuCore("FMC");
            }
            
            // Apply APU core (default to FMC if not valid)
            if (!string.IsNullOrEmpty(config.SelectedApuCore)
                && CoreRegistry.ApuIds.Contains(config.SelectedApuCore)
                && IsApuCoreUnlocked(config.SelectedApuCore, progressionSave))
            {
                nes.SetApuCore(config.SelectedApuCore);
            }
            else
            {
                Helpers.ConfigHelper.Update(config, c => c.SelectedApuCore = "FMC");
                nes.SetApuCore("FMC");
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
                    if (availableShaders.Contains(config.CurrentShader) && IsShaderUnlocked(config.CurrentShader, progressionSave))
                    {
                        NesShaderControl.SwitchShader(config.CurrentShader);
                    }
                    else
                    {
                        Helpers.ConfigHelper.Update(config, c => c.CurrentShader = ResolveUnlockedShaderSelection(config.CurrentShader, availableShaders, progressionSave.OwnedShaderIds, "PX"));
                        if (availableShaders.Contains(config.CurrentShader) && IsShaderUnlocked(config.CurrentShader, progressionSave))
                        {
                            NesShaderControl.SwitchShader(config.CurrentShader);
                        }
                    }
                }
                
                // Restore shader strength
                if (config.ShaderStrength > 0)
                {
                    NesShaderControl.SetShaderStrength(config.ShaderStrength);
                }
            }
        }
    }
}
