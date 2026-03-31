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
        private void About_Click(object? sender, EventArgs e)
        {
            MessageBox.Show(
                "BrokenNes is a NES emulator and corruption toolkit, built loosely with care and love and attention to detal.\n\n" +
                "Inspired by work on NET‑NES, QuickNES, and NesHawk. More info on that will be added later if I feel like it.\n\n" +
                "Achievements are based on RetroAchievements. Certain have beed lifted, reimagined or modified. Search Retroachievements.org for individual credits.\n\n" +
                "Design and architecture: ircluzar",
                "About BrokenNes",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private async void TakeScreenshot_Click(object? sender, EventArgs e)
        {
             if (nes == null) return;

             // Capture synchronously to ensure consistency
             Bitmap? screenshot = null;
             string? stateJson = null;
             string localRomPath = currentRomPath;

             try
             {
                 stateJson = await CaptureStateNowAsync();
                 if (string.IsNullOrEmpty(stateJson))
                 {
                     Console.WriteLine("Screenshot capture failed: atomic snapshot timed out");
                     return;
                 }
                 lock (emulationLock)
                 {
                     screenshot = GetScreenshot();
                 }
             }
             catch (Exception ex)
             {
                 Console.WriteLine($"Screenshot capture failed: {ex.Message}");
                 return;
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
                             Console.WriteLine("Screenshot Saved");
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
                Console.WriteLine("Emulator Paused");
            }
            else
            {
                audioManager?.Play();
                Console.WriteLine("Emulator Resumed");
            }
        }

        /// <summary>
        /// Resume emulation if it is currently paused and a game is loaded.
        /// Called by the Web API when Story mode or other overlay modules need the emulator running.
        /// </summary>
        private void ResumeEmulationIfPaused()
        {
            // Check if there's a loaded game (not test ROM) and it's paused
            bool hasLoadedGame = nes != null && !string.IsNullOrEmpty(currentRomPath);
            bool isTestRom = !string.IsNullOrEmpty(currentRomPath) && currentRomPath.Contains("test.nes", StringComparison.OrdinalIgnoreCase);
            
            if (hasLoadedGame && !isTestRom && isPaused && isEmulationRunning)
            {
                Console.WriteLine("[ResumeEmulationIfPaused] Resuming paused emulator via API");
                isPaused = false;
                audioManager?.Play();
                Console.WriteLine("Emulator Resumed (API call)");
            }
            else if (!isEmulationRunning && hasLoadedGame && !isTestRom)
            {
                // If emulation is not running but we have a game loaded, start it
                Console.WriteLine("[ResumeEmulationIfPaused] Starting emulation via API");
                StartEmulation();
            }
            else
            {
                Console.WriteLine($"[ResumeEmulationIfPaused] No action needed - hasLoadedGame={hasLoadedGame}, isTestRom={isTestRom}, isPaused={isPaused}, isEmulationRunning={isEmulationRunning}");
            }
        }

        /// <summary>
        /// Pause emulation if it is currently running and a game is loaded.
        /// Used by overlay modules that need a static frame for interaction.
        /// </summary>
        private void PauseEmulationIfRunning()
        {
            bool hasLoadedGame = nes != null && !string.IsNullOrEmpty(currentRomPath);
            bool isTestRom = !string.IsNullOrEmpty(currentRomPath) && currentRomPath.Contains("test.nes", StringComparison.OrdinalIgnoreCase);

            if (hasLoadedGame && !isTestRom && isEmulationRunning && !isPaused)
            {
                Console.WriteLine("[PauseEmulationIfRunning] Pausing emulator via API");
                isPaused = true;
                audioManager?.Stop();
                Console.WriteLine("Emulator Paused (API call)");
            }
            else
            {
                Console.WriteLine($"[PauseEmulationIfRunning] No action needed - hasLoadedGame={hasLoadedGame}, isTestRom={isTestRom}, isPaused={isPaused}, isEmulationRunning={isEmulationRunning}");
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

        private void ToggleAcceptBackgroundInput_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.AcceptBackgroundInput = menuItem.Checked);

                Console.WriteLine($"Accept Background Input set to: {config.AcceptBackgroundInput}");

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

        private void ToggleShowWebmodulesMenu_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.ShowWebmodulesMenu = menuItem.Checked);
                
                // Update menu visibility
                if (webModulesMenu != null)
                {
                    webModulesMenu.Visible = config.ShowWebmodulesMenu;
                }
                
                Console.WriteLine($"Show Webmodules Menu set to: {config.ShowWebmodulesMenu}");
                
                UpdateConfigMenus();
            }
        }

        private void ToggleShowLockedItems_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.ShowLockedItems = menuItem.Checked);

                var save = LoadProgressionSnapshot();
                RebuildBackgroundMenu(save);
                RebuildNullProviderMenu(save);
                RebuildToolsMenu(save);
                RebuildWebModulesMenu(save);
                UpdateCoresMenus();
                UpdateConfigMenus();
            }
        }

        private void ToggleWebmoduleSavestateDebugShortcuts_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.EnableWebmoduleSavestateDebugShortcuts = menuItem.Checked);
                Console.WriteLine($"Webmodule savestate debug shortcuts set to: {config.EnableWebmoduleSavestateDebugShortcuts}");
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
