using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
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
                    stateDict["uiShadersEnabled"] = true;
                    stateDict["uiCurrentShader"] = config.CurrentShader ?? string.Empty;
                    stateDict["uiShaderStrength"] = config.ShaderStrength;
                }
                else
                {
                    stateDict["uiShadersEnabled"] = true;
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
        
        private bool SaveDeckContinueState()
        {
             if (nes == null) return false;
             var continuePath = GetDeckContinueStatePathForCurrentRom();
             if (string.IsNullOrWhiteSpace(continuePath)) return false;
               var tempPath = continuePath + ".tmp";

             try
             {
                 Directory.CreateDirectory(Path.GetDirectoryName(continuePath)!);
                 string? stateJson = nes.CaptureAtomicSnapshot(2000);
                 if (string.IsNullOrEmpty(stateJson)) return false;

                 Bitmap? screenshot;
                 lock (emulationLock)
                 {
                     screenshot = GetScreenshot();
                 }

                 using (screenshot)
                 {
                     if (screenshot != null)
                     {
                         byte[] stateBytes = Encoding.UTF8.GetBytes(stateJson);
                         using (Bitmap embedded = PngPayload.EmbedData(screenshot, stateBytes))
                         {
                             if (embedded == null)
                             {
                                 return false;
                             }

                             // Write to a temp file first so an existing checkpoint remains intact on failures.
                             embedded.Save(tempPath, ImageFormat.Png);
                         }

                         File.Copy(tempPath, continuePath, true);
                         Console.WriteLine($"Deck continue saved to {continuePath}");
                         return File.Exists(continuePath);
                     }
                 }

                 return false;
             }
             catch (Exception ex)
             {
                 Console.WriteLine("Failed to save deck continue state: " + ex.Message);
                 return false;
             }
             finally
             {
                 try
                 {
                     if (File.Exists(tempPath))
                     {
                         File.Delete(tempPath);
                     }
                 }
                 catch
                 {
                     // Best-effort cleanup only.
                 }
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
                bool hasCurrentShader = root.TryGetProperty("uiCurrentShader", out var currentShaderEl);
                bool hasShaderStrength = root.TryGetProperty("uiShaderStrength", out var shaderStrengthEl);
                
                if (!hasCurrentShader && !hasShaderStrength)
                {
                    // Old savestate without UI settings - skip restoration
                    Console.WriteLine("Savestate does not contain UI settings - keeping current configuration");
                }
                else
                {
                    // Restore shader settings if DirectX is available
                    if (useDirectX && dxRenderer != null)
                    {
                        // Shaders are always-on.
                        dxRenderer.UseShader = true;
                        config.ShadersEnabled = true;
                        Console.WriteLine("Restored shaders enabled: True");
                        
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
                
                Console.WriteLine($"State Loaded: {Path.GetFileName(currentRomPath)}");
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
        
        private async void SaveStateToFile_Click(object? sender, EventArgs e)
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
                    string? stateJson = await nes.CaptureAtomicSnapshotAsync(2000);
                    if (string.IsNullOrEmpty(stateJson))
                    {
                        MessageBox.Show("Failed to capture atomic savestate.", "Save State Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    Bitmap? screenshot;
                    lock (emulationLock)
                    {
                        screenshot = GetScreenshot();
                    }

                    // Extend state with UI settings (shader config)
                    stateJson = ExtendStateWithUISettings(stateJson);

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

                    Console.WriteLine($"State Saved: {Path.GetFileName(currentRomPath)}");
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
            TryQuickSaveState(showDialogs: true);
        }
        
        private void QuickLoadState_Click(object? sender, EventArgs e)
        {
            TryQuickLoadState(showDialogs: true);
        }

        private bool TryQuickSaveState(bool showDialogs)
        {
            if (nes == null)
            {
                if (showDialogs)
                {
                    MessageBox.Show("Please load a ROM first.", "No ROM Loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return false;
            }

            try
            {
                quickSaveState = nes.CaptureAtomicSnapshot(2000);
                if (string.IsNullOrEmpty(quickSaveState))
                {
                    if (showDialogs)
                    {
                        MessageBox.Show("Failed to capture atomic savestate.", "Quick Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return false;
                }

                quickSaveState = ExtendStateWithUISettings(quickSaveState);
                Console.WriteLine($"Quick Saved: {Path.GetFileName(currentRomPath)}");
                return true;
            }
            catch (Exception ex)
            {
                if (showDialogs)
                {
                    MessageBox.Show($"Failed to quick save state:\n{ex.Message}", "Quick Save Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Console.WriteLine("Failed to quick save state: " + ex.Message);
                return false;
            }
        }

        private bool TryQuickLoadState(bool showDialogs)
        {
            if (nes == null)
            {
                if (showDialogs)
                {
                    MessageBox.Show("Please load a ROM first.", "No ROM Loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return false;
            }

            if (string.IsNullOrEmpty(quickSaveState))
            {
                if (showDialogs)
                {
                    MessageBox.Show("No quick save state available. Use Quick Save State (F7) first.",
                        "No Quick Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return false;
            }

            bool wasPaused = isPaused;
            isPaused = true;

            try
            {
                lock (emulationLock)
                {
                    nes.LoadState(quickSaveState);
                }

                RestoreUISettingsFromState(quickSaveState);
                BuildMemoryDomains();
                UpdateCoresMenus();
                Console.WriteLine($"Quick Loaded: {Path.GetFileName(currentRomPath)}");
                return true;
            }
            catch (Exception ex)
            {
                if (showDialogs)
                {
                    MessageBox.Show($"Failed to quick load state:\n{ex.Message}", "Quick Load Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Console.WriteLine("Failed to quick load state: " + ex.Message);
                return false;
            }
            finally
            {
                isPaused = wasPaused;
            }
        }

        private bool QuickSaveStateFromApi()
        {
            if (!config.EnableWebmoduleSavestateDebugShortcuts)
            {
                return false;
            }

            return TryQuickSaveState(showDialogs: false);
        }

        private bool QuickLoadStateFromApi()
        {
            if (!config.EnableWebmoduleSavestateDebugShortcuts)
            {
                return false;
            }

            return TryQuickLoadState(showDialogs: false);
        }

        private void SaveContinueState()
        {
             if (nes == null) return;
               var continuePath = GetGenericContinueStatePathForCurrentRom();
             if (string.IsNullOrWhiteSpace(continuePath)) return;
             var legacyContinuePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "continue.png");
             
             try
             {
                 Directory.CreateDirectory(Path.GetDirectoryName(continuePath)!);
                 string? stateJson = nes.CaptureAtomicSnapshot(2000);
                 if (string.IsNullOrEmpty(stateJson)) return;

                 Bitmap? screenshot;
                 lock (emulationLock)
                 {
                     screenshot = GetScreenshot();
                 }

                 using (screenshot)
                 {
                     if (screenshot != null)
                     {
                         byte[] stateBytes = Encoding.UTF8.GetBytes(stateJson);
                         using (Bitmap embedded = PngPayload.EmbedData(screenshot, stateBytes))
                         {
                             embedded?.Save(continuePath, ImageFormat.Png);
                             try { embedded?.Save(legacyContinuePath, ImageFormat.Png); } catch { }
                         }
                         Console.WriteLine($"Game saved to {continuePath}");
                     }
                 }
             }
             catch (Exception ex) 
             {
                 Console.WriteLine("Failed to save continue state: " + ex.Message);
             }
        }

        private bool SaveContinueStateFromApi()
        {
            try
            {
                var saved = SaveDeckContinueState();
                if (saved)
                {
                    PauseEmulationIfRunning();
                }

                return saved;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to save continue state via API: " + ex.Message);
                return false;
            }
        }

        private bool LoadContinueStateFromApi(string? romKey)
        {
            try
            {
                var continuePath = ResolveDeckContinueStatePathForRom(romKey);
                if (!File.Exists(continuePath)) return false;
                LoadStateFile(continuePath);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to load continue state via API: " + ex.Message);
                return false;
            }
        }

        private string ResolveDeckContinueStatePathForRom(string? romKey)
        {
            var perRomPath = GetDeckContinueStatePathForRom(romKey);
            if (!string.IsNullOrWhiteSpace(perRomPath) && File.Exists(perRomPath))
            {
                return perRomPath;
            }

            return string.Empty;
        }

        private string? GetGenericContinueStatePathForCurrentRom()
        {
            var romKey = GetCurrentContinueRomKey();
            return string.IsNullOrWhiteSpace(romKey) ? null : GetContinueStatePath("ContinueStates", romKey);
        }

        private string? GetDeckContinueStatePathForCurrentRom()
        {
            var romKey = GetCurrentContinueRomKey();
            return string.IsNullOrWhiteSpace(romKey) ? null : GetContinueStatePath("DeckContinueStates", romKey);
        }

        private string? GetDeckContinueStatePathForRom(string? romKey)
        {
            var resolvedRomKey = string.IsNullOrWhiteSpace(romKey) ? GetCurrentContinueRomKey() : romKey;
            return string.IsNullOrWhiteSpace(resolvedRomKey) ? null : GetContinueStatePath("DeckContinueStates", resolvedRomKey);
        }

        private string GetContinueStatePath(string directoryName, string romKey)
        {
            var continueDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BrokenNes",
                directoryName
            );

            var normalized = romKey.Trim().ToLowerInvariant();
            var fileName = Path.GetFileName(normalized);
            var sb = new StringBuilder(fileName.Length);
            foreach (var ch in fileName)
            {
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }

            var prefix = sb.ToString().Trim('_');
            if (prefix.Length > 48)
            {
                prefix = prefix[..48];
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
            var safeName = string.IsNullOrWhiteSpace(prefix)
                ? hash[..16]
                : $"{prefix}-{hash[..16]}";
            return Path.Combine(continueDir, $"{safeName}.png");
        }

        private string? GetCurrentContinueRomKey()
        {
            if (!string.IsNullOrWhiteSpace(currentRomPath))
            {
                return Path.GetFileName(currentRomPath);
            }

            return string.IsNullOrWhiteSpace(nes?.RomName) ? null : nes.RomName;
        }
    }
}
