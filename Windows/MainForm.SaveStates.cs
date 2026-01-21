using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NesEmulator;
using NesEmulator.Shaders;
using BrokenNes.Windows.Rendering;
using PngPayloadEmbedding;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
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
    }
}
