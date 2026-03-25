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
        private sealed class StoredRomRecord
        {
            public string? Name { get; set; }
            public string? Base64 { get; set; }
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
        
        private void ResetGame()
        {
            if (!string.IsNullOrEmpty(currentRomPath) && File.Exists(currentRomPath))
            {
                LoadRomFile(currentRomPath);
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

                LoadRomBytes(Path.GetFileName(path), romData, path, addToRecentRoms: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading ROM: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadRomBytes(string romName, byte[] romData, string? romPath = null, bool addToRecentRoms = false)
        {
            if (romData == null || romData.Length == 0)
            {
                throw new InvalidOperationException("ROM data is empty.");
            }

            // Stop current emulation if running
            StopEmulation();

            // Create new NES instance
            lock (emulationLock)
            {
                nes = new NES();
                nes.LoadROM(romData);
                nes.RomName = romName;
                nes.RomPath = romPath ?? romName;
                InitializeImagineEngine();
            }

            currentRomPath = romPath ?? romName;
            Console.WriteLine($"ROM Loaded: {romName}");

            if (addToRecentRoms && !string.IsNullOrWhiteSpace(romPath))
            {
                config.AddRecentRom(romPath);
            }

            ApplySavedCoreSelections();
            ApplyCrashBehavior();
            EnsureUnlockedProgressionSelections();
            nes.SetNullProvider(config.SelectedNullProvider);
            ApplyImageSettings();
            BuildMemoryDomains();
            ApplySoundSettings();
            UpdateCoresMenus();

            var fileMenu = this.MainMenuStrip?.Items.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Text == "&Emulator");
            if (fileMenu != null)
            {
                var recentMenu = fileMenu.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Text.Contains("Recent"));
                if (recentMenu != null)
                {
                    UpdateRecentRomsMenu(recentMenu);
                }
            }

            StartEmulation();
        }

        private void LoadRomBytesFromApi(string romName, byte[] romData)
        {
            LoadRomBytes(romName, romData, romName, addToRecentRoms: false);
        }
        
        private void CloseRom_Click(object? sender, EventArgs e)
        {
            if (nes == null || string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase)) 
                return;
            
            Console.WriteLine($"Closing current ROM: {nes.RomName}");
            
            // Save state for the current game
            SaveContinueState();

            // Stop current emulation
            StopEmulation();
            
            // Transition back to the base launcher state (test.nes)
            // Close ROM is a special case: stay in emulator mode.
            LoadEmbeddedRom(allowHomeWebModule: false);
            
            // Re-show the continue button if a save state exists
            ShowContinueButton();
            
            // Update UI menus to reflect the base state
            UpdateCoresMenus();
            
            // Force a layout update and refresh
            this.PerformLayout();
            this.Refresh();
        }

        private void CloseRomFromApi()
        {
            if (nes == null || string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase))
                return;

            Console.WriteLine($"[WebApi] Closing current ROM without saving continue checkpoint: {nes.RomName}");

            // Intentionally do not save continue state here; API-driven transitions may be
            // preparing to restore a trusted continue checkpoint immediately afterward.
            StopEmulation();
            LoadEmbeddedRom(allowHomeWebModule: false);

            HideContinueButton();
            UpdateCoresMenus();

            this.PerformLayout();
            this.Refresh();
        }

        private bool LoadRomFromApi(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[WebApi] ROM file not found: {path}");
                    return false;
                }

                LoadRomFile(path);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebApi] Failed to load ROM from path: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> LoadRomKeyFromApiAsync(string romKey)
        {
            if (string.IsNullOrWhiteSpace(romKey)) return false;

            var normalizedKey = romKey.Trim();

            try
            {
                if (webView?.CoreWebView2 != null)
                {
                    var keyJson = System.Text.Json.JsonSerializer.Serialize(normalizedKey);
                    var script = $@"(async()=>{{
    try {{
        const target = {keyJson};
        const normalizedTarget = typeof target === 'string' ? target.toLowerCase() : '';

        async function readFromIndexedDb() {{
            if (!window.indexedDB) {{
                return null;
            }}

            const db = await new Promise((resolve, reject) => {{
                const req = indexedDB.open('nesStorage', 1);
                req.onsuccess = () => resolve(req.result);
                req.onerror = () => reject(req.error || new Error('IndexedDB open error'));
            }});

            return await new Promise((resolve, reject) => {{
                try {{
                    const tx = db.transaction('roms', 'readonly');
                    const store = tx.objectStore('roms');
                    const directReq = store.get(target);
                    directReq.onsuccess = () => {{
                        const direct = directReq.result;
                        if (direct && typeof direct.name === 'string' && direct.base64) {{
                            resolve(direct);
                            return;
                        }}

                        if ('getAll' in store) {{
                            const allReq = store.getAll();
                            allReq.onsuccess = () => {{
                                const rows = Array.isArray(allReq.result) ? allReq.result : [];
                                resolve(rows.find(entry => entry && typeof entry.name === 'string' && entry.name.toLowerCase() === normalizedTarget) || null);
                            }};
                            allReq.onerror = () => reject(allReq.error || new Error('IndexedDB read error'));
                            return;
                        }}

                        const rows = [];
                        const cursorReq = store.openCursor();
                        cursorReq.onsuccess = (event) => {{
                            const cursor = event.target.result;
                            if (cursor) {{
                                rows.push(cursor.value);
                                cursor.continue();
                                return;
                            }}

                            resolve(rows.find(entry => entry && typeof entry.name === 'string' && entry.name.toLowerCase() === normalizedTarget) || null);
                        }};
                        cursorReq.onerror = () => reject(cursorReq.error || new Error('IndexedDB cursor error'));
                    }};
                    directReq.onerror = () => reject(directReq.error || new Error('IndexedDB direct lookup error'));
                }} catch (error) {{
                    reject(error);
                }}
            }});
        }}

        async function readFromInterop() {{
            if (!window.nesInterop || typeof window.nesInterop.getStoredRoms !== 'function') {{
                return null;
            }}

            const roms = await window.nesInterop.getStoredRoms();
            if (!Array.isArray(roms)) {{
                return null;
            }}

            return roms.find(entry => entry && typeof entry.name === 'string' && entry.name.toLowerCase() === normalizedTarget) || null;
        }}

        async function readFromLegacyLocalStorage() {{
            try {{
                if (!window.localStorage) {{
                    return null;
                }}

                const base64 = localStorage.getItem('rom_' + target);
                if (base64) {{
                    return {{ name: target, base64 }};
                }}

                for (let index = 0; index < localStorage.length; index++) {{
                    const key = localStorage.key(index);
                    if (!key || !key.startsWith('rom_')) {{
                        continue;
                    }}

                    const storedName = key.substring(4);
                    if (storedName.toLowerCase() === normalizedTarget) {{
                        return {{ name: storedName, base64: localStorage.getItem(key) }};
                    }}
                }}
            }} catch (error) {{
                return null;
            }}

            return null;
        }}

        return await readFromIndexedDb()
            || await readFromInterop()
            || await readFromLegacyLocalStorage();
    }} catch (e) {{
        return null;
    }}
}})()";

                    var resultJson = await webView.CoreWebView2.ExecuteScriptAsync(script);
                    if (!string.IsNullOrWhiteSpace(resultJson) && !string.Equals(resultJson, "null", StringComparison.OrdinalIgnoreCase))
                    {
                        var stored = System.Text.Json.JsonSerializer.Deserialize<StoredRomRecord>(resultJson, new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                        if (!string.IsNullOrWhiteSpace(stored?.Base64))
                        {
                            var romBytes = Convert.FromBase64String(stored.Base64);
                            LoadRomBytes(stored.Name ?? normalizedKey, romBytes, stored.Name ?? normalizedKey, addToRecentRoms: false);
                            return true;
                        }
                    }
                }

                if (File.Exists(normalizedKey))
                {
                    LoadRomFile(normalizedKey);
                    return true;
                }

                return await LoadBuiltInRomAsync(normalizedKey, preserveShader: false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebApi] Failed to load ROM by key '{normalizedKey}': {ex.Message}");
                return false;
            }
        }
        
        private void LoadEmbeddedRom(bool allowHomeWebModule = true)
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
                    lock (emulationLock)
                    {
                        nes = new NES();
                        nes.LoadROM(romData);
                        nes.RomName = "test.nes";
                    }
                    
                    currentRomPath = "test.nes (embedded)";
                    Console.WriteLine("ROM Loaded: test.nes");
                    
                    // Apply saved core selections
                    ApplySavedCoreSelections();
                    EnsureUnlockedProgressionCapabilities();
                    
                    // Apply crash behavior
                    ApplyCrashBehavior();
                    
                    // Apply saved null provider
                    EnsureUnlockedProgressionSelections();
                    nes.SetNullProvider(config.SelectedNullProvider);

                    // Apply image settings (will force Pixel Perfect for Test ROM)
                    ApplyImageSettings();
                    
                    // Update cores menus
                    UpdateCoresMenus();
                    
                    // Start emulation automatically
                    StartEmulation();
                    
                    // Check if we should load the Home webmodule instead of staying in emulator mode
                    if (allowHomeWebModule && !config.BootToEmulator)
                    {
                        // Load the Home webmodule
                        LoadHomeWebModule();
                    }
                    
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

        /// <summary>
        /// Load a built-in ROM file from wwwroot (for story mode and other built-in content)
        /// Returns true on success
        /// </summary>
        /// <param name="preserveShader">If true, preserve current shader settings during load</param>
        public async Task<bool> LoadBuiltInRomAsync(string filename, bool preserveShader = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filename))
                    return false;

                // Hide continue button if it's being displayed (story mode shouldn't show it)
                HideContinueButton();

                Console.WriteLine($"[Story] Loading built-in page ROM: {filename}");

                // Save shader state if preserving
                Rendering.NesShaderManager.ShaderType? savedShader = null;
                bool? savedShaderEnabled = null;
                if (preserveShader && useDirectX && dxRenderer != null)
                {
                    savedShader = dxRenderer.CurrentShaderType;
                    savedShaderEnabled = dxRenderer.UseShader;
                    Console.WriteLine($"[Story] Preserving shader: {savedShader}, Enabled: {savedShaderEnabled}");
                }

                // Check Data/story folder first (Windows build location)
                string dataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "story", filename);
                
                // Fall back to wwwroot if not found
                string wwwrootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", filename);
                
                string romPath = File.Exists(dataPath) ? dataPath : wwwrootPath;
                
                if (!File.Exists(romPath))
                {
                    Console.WriteLine($"[Story] Page ROM not found in either location:");
                    Console.WriteLine($"  - {dataPath}");
                    Console.WriteLine($"  - {wwwrootPath}");
                    return false;
                }

                Console.WriteLine($"[Story] Loading ROM from: {romPath}");
                byte[] romData = await Task.Run(() => File.ReadAllBytes(romPath));
                
                if (romData == null || romData.Length == 0)
                {
                    Console.WriteLine($"[Story] Page ROM empty: {filename}");
                    return false;
                }

                bool wasRunning = false;
                
                // Execute on UI thread
                await InvokeAsync(() =>
                {
                    // CRITICAL: Check if running and stop OUTSIDE the lock to avoid deadlock
                    // The emulation thread needs to acquire emulationLock briefly, so we can't hold it while waiting
                    wasRunning = isEmulationRunning;
                    
                    if (wasRunning)
                    {
                        StopEmulation(); // This already waits for thread to stop
                    }
                    
                    // Now that thread is stopped, we can safely acquire lock and modify NES state
                    lock (emulationLock)
                    {
                        if (nes == null)
                            nes = new NES();
                        
                        nes.RomName = filename;
                        nes.RomPath = romPath;
                        nes.LoadROM(romData);
                        
                        // Apply core selections
                        ApplySavedCoreSelections();
                        EnsureUnlockedProgressionCapabilities();
                        
                        // Apply crash behavior
                        ApplyCrashBehavior();
                        
                        // Warm up a frame to avoid stale canvas
                        try
                        {
                            nes.RunFrame();
                            // Redraw the canvas
                            displayPanel?.Invalidate();
                            dxRenderer?.Invalidate();
                        }
                        catch { }
                    }
                    
                    // ALWAYS start emulation for built-in ROMs (story mode needs emulator running)
                    // This ensures the emulator is running even when loaded from Web mode
                    if (!isEmulationRunning)
                    {
                        StartEmulation();
                    }
                    
                    // Restore shader if we were preserving it
                    if (preserveShader && savedShader.HasValue && savedShaderEnabled.HasValue && useDirectX && dxRenderer != null)
                    {
                        dxRenderer.SwitchShader(savedShader.Value);
                        dxRenderer.UseShader = true;
                        Console.WriteLine($"[Story] Restored shader: {savedShader}, Enabled: True");
                    }
                });

                Console.WriteLine($"[Story] Successfully loaded page ROM: {filename}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Story] Failed to load page ROM: {filename}, Error: {ex.Message}");
                return false;
            }
        }

        private Task InvokeAsync(Action action)
        {
            if (InvokeRequired)
            {
                var tcs = new TaskCompletionSource<bool>();
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        action();
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }));
                return tcs.Task;
            }
            else
            {
                action();
                return Task.CompletedTask;
            }
        }
    }
}
