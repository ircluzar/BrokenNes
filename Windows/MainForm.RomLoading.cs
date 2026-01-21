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
            this.Text = "BrokenNes";
            
            // Clear the display
            if (displayPanel.InvokeRequired)
            {
                displayPanel.Invoke((Action)(() => displayPanel.Invalidate()));
            }
            else
            {
                displayPanel.Invalidate();
            }
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
    }
}
