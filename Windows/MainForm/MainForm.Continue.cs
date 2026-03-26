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
        private void ShowContinueButton()
        {
            try 
            {
                string continuePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "continue.png");
                if (!File.Exists(continuePath) || continueButton != null) return;

                // Load to memory so we don't lock the file
                Bitmap img;
                using (var fs = new FileStream(continuePath, FileMode.Open, FileAccess.Read))
                {
                    using (var temp = new Bitmap(fs))
                    {
                        img = new Bitmap(temp);
                    }
                }
                    
                using (Graphics g = Graphics.FromImage(img))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                    string text = "Continue?";
                    using (Font f = new Font("Segoe UI", 16, FontStyle.Bold))
                    {
                            // Thicker and blurrier shadow
                            using (var shadowBrush = new SolidBrush(Color.FromArgb(30, Color.Black)))
                            {
                                for (int y = 1; y <= 5; y++)
                                {
                                    for (int x = 1; x <= 5; x++)
                                    {
                                        g.DrawString(text, f, shadowBrush, new PointF(10 + x, 10 + y));
                                    }
                                }
                            }
                             
                            g.DrawString(text, f, Brushes.White, new PointF(10, 10));
                        }

                        // Outline surrounding the continue box
                        using (var outlinePen = new Pen(Color.White, 3))
                        {
                            outlinePen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;
                            g.DrawRectangle(outlinePen, 0, 0, img.Width, img.Height);
                        }
                    }

                continueButton = new PictureBox
                {
                    Image = img,
                    SizeMode = PictureBoxSizeMode.AutoSize,
                    Cursor = Cursors.Hand,
                    Location = new Point(20, 20),
                    BackColor = Color.Transparent
                };
                continueButton.Click += ContinueSession_Click;
                
                displayPanel.Controls.Add(continueButton);
                continueButton.BringToFront();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load continue.png: {ex.Message}");
            }
        }

        private void HideContinueButton()
        {
            if (continueButton != null)
            {
                if (displayPanel.Controls.Contains(continueButton))
                    displayPanel.Controls.Remove(continueButton);
                
                if (continueButton.Image != null) continueButton.Image.Dispose();
                continueButton.Dispose();
                continueButton = null;
            }
        }

        private void ContinueSession_Click(object? sender, EventArgs e)
        {
             HideContinueButton();
             string continuePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "continue.png");
             if (File.Exists(continuePath))
             {
                 if (TryLaunchDeckBuilderContinueFromScreenshot(continuePath))
                 {
                     try { File.Delete(continuePath); } catch {}
                     return;
                 }

                 if (TryLaunchDeckBuilderContinueFromProgression())
                 {
                     try { File.Delete(continuePath); } catch {}
                     return;
                 }

                 LoadStateFile(continuePath);
                 // Delete after loading so it doesn't appear again on next launch unless saved again
                 try { File.Delete(continuePath); } catch {}
             }
        }

        private bool TryLaunchDeckBuilderContinueFromProgression()
        {
            try
            {
                var save = LoadProgressionSnapshot();
                if (save.ContinueSlots == null || save.ContinueSlots.Count == 0)
                {
                    return false;
                }

                var latestSlot = save.ContinueSlots.Values
                    .Where(slot => slot != null && !string.IsNullOrWhiteSpace(slot.RomKey))
                    .OrderByDescending(slot => slot!.UpdatedAtUtc ?? DateTime.MinValue)
                    .FirstOrDefault();
                if (latestSlot == null)
                {
                    return false;
                }

                var romKey = latestSlot.RomKey.Trim();
                if (string.IsNullOrWhiteSpace(romKey))
                {
                    return false;
                }

                var title = !string.IsNullOrWhiteSpace(latestSlot.Title)
                    ? latestSlot.Title!
                    : romKey;

                Console.WriteLine($"[Continue] Falling back to latest trusted Deck continue slot: {romKey}");
                return TryLaunchAchievementsRuntimeContinue(romKey, title);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Continue] Failed progression fallback continue handoff: {ex.Message}");
                return false;
            }
        }

        private bool TryLaunchDeckBuilderContinueFromScreenshot(string continuePath)
        {
            try
            {
                if (!TryReadSavedRomIdentityFromState(continuePath, out var savedRomPath, out var savedRomName))
                {
                    return false;
                }

                var romKey = ResolveStateRomKey(savedRomPath, savedRomName);
                if (string.IsNullOrWhiteSpace(romKey))
                {
                    return false;
                }

                var save = LoadProgressionSnapshot();
                if (!TryGetTrustedDeckContinueSlot(save, romKey, out var slot))
                {
                    return false;
                }

                var title = !string.IsNullOrWhiteSpace(slot?.Title)
                    ? slot!.Title!
                    : (!string.IsNullOrWhiteSpace(savedRomName) ? savedRomName : romKey);

                return TryLaunchAchievementsRuntimeContinue(romKey, title);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Continue] Failed Deck Builder continue handoff: {ex.Message}");
                return false;
            }
        }

        private bool TryReadSavedRomIdentityFromState(string filePath, out string? savedRomPath, out string? savedRomName)
        {
            savedRomPath = null;
            savedRomName = null;

            try
            {
                using var bmp = new Bitmap(filePath);
                var data = PngPayload.ExtractData(bmp);
                if (data == null || data.Length == 0)
                {
                    return false;
                }

                var stateJson = Encoding.UTF8.GetString(data);
                savedRomPath = NES.GetSavedRomPath(stateJson);
                savedRomName = NES.GetSavedRomName(stateJson);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Continue] Failed to inspect continue state payload: {ex.Message}");
                return false;
            }
        }

        private static string? ResolveStateRomKey(string? savedRomPath, string? savedRomName)
        {
            if (!string.IsNullOrWhiteSpace(savedRomPath))
            {
                var keyFromPath = Path.GetFileName(savedRomPath.Trim());
                if (!string.IsNullOrWhiteSpace(keyFromPath))
                {
                    return keyFromPath;
                }
            }

            return string.IsNullOrWhiteSpace(savedRomName) ? null : savedRomName.Trim();
        }

        private static string NormalizeContinueSlotKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            var fileName = Path.GetFileName(trimmed);
            var normalized = string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
            return normalized.Trim().ToLowerInvariant();
        }

        private static bool TryGetTrustedDeckContinueSlot(BrokenNes.Models.GameSave save, string romKey, out BrokenNes.Models.ContinueStateSlot? slot)
        {
            slot = null;
            var normalized = NormalizeContinueSlotKey(romKey);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (save.ContinueSlots == null || save.ContinueSlots.Count == 0)
            {
                return false;
            }

            if (save.ContinueSlots.TryGetValue(normalized, out var direct) && direct != null)
            {
                slot = direct;
                return true;
            }

            foreach (var entry in save.ContinueSlots.Values)
            {
                if (entry == null)
                {
                    continue;
                }

                if (NormalizeContinueSlotKey(entry.RomKey) == normalized)
                {
                    slot = entry;
                    return true;
                }
            }

            return false;
        }

        private bool TryLaunchAchievementsRuntimeContinue(string romKey, string title)
        {
            if (!Helpers.WebViewHelper.IsAvailable(webView, isWebViewInitialized, isWebViewInitializationFailed))
            {
                return false;
            }

            try
            {
                EnsureWebApiServerRunningAsync().GetAwaiter().GetResult();

                var webModules = WebModuleManager.DiscoverModules();
                var runtimeModule = webModules.FirstOrDefault(m =>
                    string.Equals(m.FolderName, "AchievementsRuntime", StringComparison.OrdinalIgnoreCase));
                if (runtimeModule == null || !runtimeModule.IsValid)
                {
                    Console.WriteLine("[Continue] AchievementsRuntime module not found.");
                    return false;
                }

                if (!IsWebModuleUnlocked(runtimeModule))
                {
                    Console.WriteLine("[Continue] AchievementsRuntime is locked; falling back to native continue load.");
                    return false;
                }

                if (this.MainMenuStrip != null)
                {
                    this.MainMenuStrip.Visible = !runtimeModule.HideMenuBar;
                }

                ViewMode targetMode = runtimeModule.DisplayMode switch
                {
                    WebModuleDisplayMode.Widget => ViewMode.Widget,
                    WebModuleDisplayMode.Overlay => ViewMode.Overlay,
                    WebModuleDisplayMode.Web => ViewMode.Web,
                    _ => ViewMode.Web
                };

                SwitchViewMode(targetMode, skipNavigation: true);

                var uri = runtimeModule.GetVirtualHostUri();
                var separator = uri.Contains('?') ? "&" : "?";
                var launchUri = uri
                    + separator
                    + "mode=continue"
                    + "&romKey=" + Uri.EscapeDataString(romKey)
                    + "&title=" + Uri.EscapeDataString(title)
                    + "&source=emulator-continue";

                Helpers.WebViewHelper.NavigateToUri(webView, launchUri);

                if (runtimeModule.Config.ShowInToolsMenu)
                {
                    currentToolOrActivityModule = runtimeModule;
                }

                Console.WriteLine($"[Continue] Routed trusted Deck continue to AchievementsRuntime for {romKey}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Continue] Failed to launch AchievementsRuntime continue: {ex.Message}");
                return false;
            }
        }
    }
}
