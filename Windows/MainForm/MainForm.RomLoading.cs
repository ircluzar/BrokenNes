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
                Console.WriteLine($"ROM Loaded: {Path.GetFileName(path)}");
                
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
        public async Task<bool> LoadBuiltInRomAsync(string filename)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filename))
                    return false;

                Console.WriteLine($"[Story] Loading built-in page ROM: {filename}");

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
                    
                    // Start emulation outside lock as well
                    if (wasRunning)
                        StartEmulation();
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
