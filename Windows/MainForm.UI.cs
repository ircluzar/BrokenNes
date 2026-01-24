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

        private void TakeScreenshot_Click(object? sender, EventArgs e)
        {
             if (nes == null) return;

             // Capture synchronously to ensure consistency
             Bitmap? screenshot = null;
             string? stateJson = null;
             string localRomPath = currentRomPath;

             lock(emulationLock)
             {
                 try
                 {
                     screenshot = GetScreenshot();
                     stateJson = nes.SaveState();
                 }
                 catch (Exception ex)
                 {
                     Console.WriteLine($"Screenshot capture failed: {ex.Message}");
                     return;
                 }
             }

             if (screenshot == null || stateJson == null) return;

             // Offload IO and processing to background task to avoid hitching
             Task.Run(() => 
             {
                 try
                 {
                     string screenshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
                     Directory.CreateDirectory(screenshotsDir);

                     string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff");
                     string filename = $"BrokenNes_{Path.GetFileNameWithoutExtension(localRomPath)}_{timestamp}.png";
                     string fullPath = Path.Combine(screenshotsDir, filename);

                     byte[] stateBytes = Encoding.UTF8.GetBytes(stateJson);
                     
                     using (screenshot)
                     {
                         // Embed state for sharing capabilities
                         using (Bitmap embedded = PngPayload.EmbedData(screenshot, stateBytes))
                         {
                              if (embedded != null)
                              {
                                  embedded.Save(fullPath, ImageFormat.Png);
                              }
                              else
                              {
                                  // If embedding fails (e.g. state too big?), just save the raw screenshot
                                  screenshot.Save(fullPath, ImageFormat.Png);
                              }
                         }
                     }
                     
                     Console.WriteLine($"Screenshot saved: {fullPath}");
                     
                     // Show a brief OSD message or update title
                     if (!this.IsDisposed && this.IsHandleCreated)
                     {
                         this.Invoke((MethodInvoker)delegate 
                         {
                             string currentText = this.Text;
                             // Prevent duplicate status messages
                             string baseText = currentText.Replace(" [Screenshot Saved]", "");
                             this.Text = $"{baseText} [Screenshot Saved]";
                             
                             Task.Delay(1500).ContinueWith(_ => 
                             {
                                 if (this.IsHandleCreated && !this.IsDisposed) 
                                     this.BeginInvoke(new Action(() => this.Text = baseText));
                             });
                         });
                     }

                 }
                 catch (Exception ex)
                 {
                     Console.WriteLine($"Failed to save screenshot: {ex.Message}");
                 }
             });
        }

        private void OpenEmulatorFolder_Click(object? sender, EventArgs e)
        {
            try
            {
                string folderPath = AppDomain.CurrentDomain.BaseDirectory;
                System.Diagnostics.Process.Start("explorer.exe", folderPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open folder: {ex.Message}", "Error");
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

        private void ResetEmulator_Click(object? sender, EventArgs e)
        {
            if (nes != null && !string.IsNullOrEmpty(currentRomPath))
            {
                // Reload the ROM to reset the emulator
                LoadRomFile(currentRomPath);
            }
        }

        private void SetWindowZoom(int zoom)
        {
            Helpers.ConfigHelper.Update(config, c => c.WindowZoom = zoom);
            
            // Calculate new window size based on NES resolution and zoom
            int newWidth = NES_WIDTH * zoom;
            int newHeight = NES_HEIGHT * zoom;
            
            // Add space for menu bar (approximate)
            int menuHeight = this.MainMenuStrip?.Height ?? 24;
            
            this.ClientSize = new Size(newWidth, newHeight + menuHeight);
            
            // Update menu checkmarks
            UpdateConfigMenus();
        }

        private void ToggleShowConsole_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.ShowConsole = menuItem.Checked);
                
                Program.SetConsoleVisibility(config.ShowConsole);
                
                Console.WriteLine($"Console visibility set to: {config.ShowConsole}");
                
                UpdateConfigMenus();
            }
        }

        private void ToggleBootToEmulator_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.BootToEmulator = menuItem.Checked);
                
                Console.WriteLine($"Boot to Emulator set to: {config.BootToEmulator}");
                
                UpdateConfigMenus();
            }
        }

        private void ToggleProfiling_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.ProfilingEnabled = menuItem.Checked);
                PerformanceProfiler.Enabled = menuItem.Checked;
                
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

        private void ToggleVSync_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem && useDirectX && dxRenderer != null)
            {
                Helpers.ConfigHelper.Update(config, c => c.EnableVSync = menuItem.Checked);
                dxRenderer.EnableVSync = menuItem.Checked;
                // Note: VSync can reduce performance and may cause stuttering
                // It's off by default for maximum performance
            }
        }

        private void ToggleShowFps_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.ShowFps = menuItem.Checked);
                if (useDirectX && dxRenderer != null)
                {
                    dxRenderer.ShowFps = config.ShowFps;
                }
                UpdateConfigMenus();
            }
        }

        private void ToggleAutoScrambleCores_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.AutoScrambleCores = menuItem.Checked);
                
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
    }
}
