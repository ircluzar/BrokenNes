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
        private void ClearRuntimeCoreAndShaderOverrides(bool reapplyPersistedSelections = false)
        {
            bool hadOverrides = !string.IsNullOrWhiteSpace(runtimeCpuCoreOverride)
                || !string.IsNullOrWhiteSpace(runtimePpuCoreOverride)
                || !string.IsNullOrWhiteSpace(runtimeApuCoreOverride)
                || !string.IsNullOrWhiteSpace(runtimeShaderOverride);

            runtimeCpuCoreOverride = null;
            runtimePpuCoreOverride = null;
            runtimeApuCoreOverride = null;
            runtimeShaderOverride = null;

            if (!hadOverrides || !reapplyPersistedSelections || nes == null)
            {
                return;
            }

            ApplySavedCoreSelections();
            UpdateCoresMenus();
            UpdateConfigMenus();
        }

        private void ApplyApuCoreSelection(string coreId)
        {
            if (nes == null || string.IsNullOrWhiteSpace(coreId)) return;

            nes.SetApuCore(coreId);

            try
            {
                audioManager?.ClearBuffer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[APU] Failed to clear WinForms audio buffer during core switch: {ex.Message}");
            }

            try
            {
                if (string.Equals(coreId, "WF", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(coreId, "MNES", StringComparison.OrdinalIgnoreCase))
                {
                    if (!nes.EnableSoundFontMode(true, null))
                    {
                        Console.WriteLine($"[APU] SoundFont backend did not activate for {coreId}.");
                    }
                }
                else
                {
                    nes.EnableSoundFontMode(false, null);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[APU] Failed to configure {coreId} audio routing: {ex.Message}");
            }
        }

        private void SetCpuCore(string coreId, bool bypassProgression = false)
        {
            if (!bypassProgression && !IsCpuCoreUnlocked(coreId))
            {
                Console.WriteLine($"[Progression] CPU core locked: {coreId}");
                var fallbackCore = ResolveUnlockedCoreSelection(config.SelectedCpuCore, CoreRegistry.CpuIds, LoadProgressionSnapshot().OwnedCpuIds, "FMC");
                if (!string.Equals(config.SelectedCpuCore, fallbackCore, StringComparison.OrdinalIgnoreCase))
                {
                    Helpers.ConfigHelper.Update(config, c => c.SelectedCpuCore = fallbackCore);
                }
                UpdateCoresMenus();
                return;
            }

            if (nes == null) return;
            nes.SetCpuCore(coreId);

            if (bypassProgression)
            {
                runtimeCpuCoreOverride = coreId;
            }
            else
            {
                runtimeCpuCoreOverride = null;
                Helpers.ConfigHelper.Update(config, c => c.SelectedCpuCore = coreId);
            }

            UpdateCoresMenus(); // Refresh to update checkmarks
        }
        
        private void SetPpuCore(string coreId, bool bypassProgression = false)
        {
            if (!bypassProgression && !IsPpuCoreUnlocked(coreId))
            {
                Console.WriteLine($"[Progression] PPU core locked: {coreId}");
                var fallbackCore = ResolveUnlockedCoreSelection(config.SelectedPpuCore, CoreRegistry.PpuIds, LoadProgressionSnapshot().OwnedPpuIds, "FMC");
                if (!string.Equals(config.SelectedPpuCore, fallbackCore, StringComparison.OrdinalIgnoreCase))
                {
                    Helpers.ConfigHelper.Update(config, c => c.SelectedPpuCore = fallbackCore);
                }
                UpdateCoresMenus();
                return;
            }

            if (nes == null) return;
            nes.SetPpuCore(coreId);

            if (bypassProgression)
            {
                runtimePpuCoreOverride = coreId;
            }
            else
            {
                runtimePpuCoreOverride = null;
                Helpers.ConfigHelper.Update(config, c => c.SelectedPpuCore = coreId);
            }

            UpdateCoresMenus(); // Refresh to update checkmarks
        }
        
        private void SetApuCore(string coreId, bool bypassProgression = false)
        {
            if (!bypassProgression && !IsApuCoreUnlocked(coreId))
            {
                Console.WriteLine($"[Progression] APU core locked: {coreId}");
                var fallbackCore = ResolveUnlockedCoreSelection(config.SelectedApuCore, CoreRegistry.ApuIds, LoadProgressionSnapshot().OwnedApuIds, "FMC");
                if (!string.Equals(config.SelectedApuCore, fallbackCore, StringComparison.OrdinalIgnoreCase))
                {
                    Helpers.ConfigHelper.Update(config, c => c.SelectedApuCore = fallbackCore);
                }
                UpdateCoresMenus();
                return;
            }

            if (nes == null) return;
            ApplyApuCoreSelection(coreId);

            if (bypassProgression)
            {
                runtimeApuCoreOverride = coreId;
            }
            else
            {
                runtimeApuCoreOverride = null;
                Helpers.ConfigHelper.Update(config, c => c.SelectedApuCore = coreId);
            }

            UpdateCoresMenus(); // Refresh to update checkmarks
        }

        private void SetShaderById(string shaderId, bool bypassProgression = false)
        {
            if (!useDirectX || dxRenderer == null || string.IsNullOrWhiteSpace(shaderId)) return;

            string normalizedShaderId = NormalizeShaderId(shaderId);
            if (string.IsNullOrWhiteSpace(normalizedShaderId))
            {
                return;
            }

            if (!bypassProgression && !IsShaderUnlocked(normalizedShaderId))
            {
                Console.WriteLine($"[Progression] Shader locked: {normalizedShaderId}");
                var fallbackShader = ResolveUnlockedShaderSelection(config.CurrentShader, NesDirectXRenderer.GetAvailableShaders(), LoadProgressionSnapshot().OwnedShaderIds, "PX");
                if (!string.Equals(config.CurrentShader, fallbackShader, StringComparison.OrdinalIgnoreCase))
                {
                    Helpers.ConfigHelper.Update(config, c => c.CurrentShader = fallbackShader);
                }
                UpdateCoresMenus();
                return;
            }

            dxRenderer.UseShader = true;

            if (!NesShaderControl.SwitchShader(normalizedShaderId))
            {
                return;
            }

            if (bypassProgression)
            {
                runtimeShaderOverride = normalizedShaderId;
            }
            else
            {
                runtimeShaderOverride = null;
                Helpers.ConfigHelper.Update(config, c =>
                {
                    c.CurrentShader = normalizedShaderId;
                    c.ShadersEnabled = true;
                });
            }

            UpdateConfigMenus();
        }

        private static string NormalizeShaderId(string shaderId)
        {
            if (string.IsNullOrWhiteSpace(shaderId))
            {
                return string.Empty;
            }

            var normalized = shaderId.Trim().ToUpperInvariant();
            if (normalized.StartsWith("SHADER_", StringComparison.Ordinal))
            {
                normalized = normalized.Substring("SHADER_".Length);
            }

            // Support legacy/alternate CRT naming used by older data paths.
            if (normalized == "CRT" || normalized == "TV_SHADER")
            {
                normalized = "TV";
            }

            return normalized;
        }
        
        private void AutoScrambleTimer_Tick(object? sender, EventArgs e)
        {
            if (nes == null || !isEmulationRunning) return;
            
            try
            {
                RandomScrambleCore();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during auto-scramble: {ex.Message}");
            }
        }
        
        private void RandomScrambleCore()
        {
            // Randomly choose which core type to scramble (0=CPU, 1=PPU, 2=APU, 3=Shader)
            int coreType = scrambleRandom.Next(0, 4);
            
            switch (coreType)
            {
                case 0: // CPU
                    {
                        var cpuCores = CoreRegistry.CpuIds;
                        if (cpuCores.Count > 1)
                        {
                            var randomCore = cpuCores[scrambleRandom.Next(cpuCores.Count)];
                            Console.WriteLine($"Auto-scramble: Switching CPU to {randomCore}");
                            SetCpuCore(randomCore);
                        }
                    }
                    break;
                    
                case 1: // PPU
                    {
                        var ppuCores = CoreRegistry.PpuIds;
                        if (ppuCores.Count > 1)
                        {
                            var randomCore = ppuCores[scrambleRandom.Next(ppuCores.Count)];
                            Console.WriteLine($"Auto-scramble: Switching PPU to {randomCore}");
                            SetPpuCore(randomCore);
                        }
                    }
                    break;
                    
                case 2: // APU
                    {
                        var apuCores = CoreRegistry.ApuIds;
                        if (apuCores.Count > 1)
                        {
                            var randomCore = apuCores[scrambleRandom.Next(apuCores.Count)];
                            Console.WriteLine($"Auto-scramble: Switching APU to {randomCore}");
                            SetApuCore(randomCore);
                        }
                    }
                    break;
                    
                case 3: // Shader
                    {
                        if (useDirectX && dxRenderer != null && config.ShadersEnabled)
                        {
                            var availableShaders = NesDirectXRenderer.GetAvailableShaders().ToList();
                            if (availableShaders.Count > 1)
                            {
                                var randomShader = availableShaders[scrambleRandom.Next(availableShaders.Count)];
                                Console.WriteLine($"Auto-scramble: Switching Shader to {randomShader}");
                                NesShaderControl.SwitchShader(randomShader);
                                Helpers.ConfigHelper.Update(config, c => c.CurrentShader = randomShader);
                            }
                        }
                    }
                    break;
            }
        }
        
        private void SetNullProvider(string providerName)
        {
            if (!IsNullProviderUnlocked(providerName))
            {
                Console.WriteLine($"[Progression] Null provider locked: {providerName}");
                var fallbackProvider = ResolveUnlockedNullProviderSelection(config.SelectedNullProvider);
                if (!string.Equals(config.SelectedNullProvider, fallbackProvider, StringComparison.OrdinalIgnoreCase))
                {
                    Helpers.ConfigHelper.Update(config, c => c.SelectedNullProvider = fallbackProvider);
                }
                UpdateConfigMenus();
                return;
            }

            Helpers.ConfigHelper.Update(config, c => c.SelectedNullProvider = providerName);
            
            // Apply to current NES instance if one is running
            if (nes != null)
            {
                nes.SetNullProvider(providerName);
            }
            
            UpdateConfigMenus();
            
            Console.WriteLine($"Null provider set to: {providerName}");
        }
        
        private void SetCrashBehavior(string behavior)
        {
            var safeBehavior = ResolveUnlockedCrashBehavior(behavior);
            if (!string.Equals(safeBehavior, behavior, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[Progression] Imagine Fix is locked.");
            }

            Helpers.ConfigHelper.Update(config, c => c.CrashBehavior = safeBehavior);
            
            // Apply to current NES instance if one is running
            ApplyCrashBehavior();
            
            UpdateConfigMenus();
            
            Console.WriteLine($"Crash behavior set to: {safeBehavior}");
        }
        
        private void ApplyCrashBehavior()
        {
            if (nes == null) return;

            var save = LoadProgressionSnapshot();
            EnsureUnlockedProgressionCapabilities(save);
            var resolvedBehavior = ResolveUnlockedCrashBehavior(config.CrashBehavior, save);
            var imagineUnlocked = IsImagineBugUnlocked(save);
            
            try
            {
                lock (emulationLock)
                {
                    if (nes != null)
                    {
                        // Sync corruptor's crash behavior with config
                        corruptor.CrashBehavior = resolvedBehavior;
                        
                        switch (resolvedBehavior)
                        {
                            case "IgnoreErrors":
                                nes.SetCrashBehavior(NES.CrashBehavior.IgnoreErrors);
                                nes.SetStubbornFixEnabled(false);
                                break;
                            case "ImagineFix":
                                nes.SetCrashBehavior(NES.CrashBehavior.ImagineFix);
                                nes.SetStubbornFixEnabled(imagineUnlocked && corruptor.StubbornMode);
                                break;
                            default: // "RedScreen"
                                nes.SetCrashBehavior(NES.CrashBehavior.RedScreen);
                                nes.SetStubbornFixEnabled(false);
                                break;
                        }

                        if (!string.Equals(config.CrashBehavior, resolvedBehavior, StringComparison.OrdinalIgnoreCase))
                        {
                            Helpers.ConfigHelper.Update(config, c => c.CrashBehavior = resolvedBehavior);
                        }

                        if (imagineEngine != null && imagineUnlocked)
                        {
                            nes.ImagineShot = pc =>
                            {
                                try { imagineEngine.ImagineFromPc(pc, Math.Clamp(corruptor.CorruptIntensity, 1, 32)); }
                                catch (Exception ex) { Console.WriteLine($"ImagineShot error: {ex.Message}"); }
                            };
                        }
                        else
                        {
                            nes.ImagineShot = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error applying crash behavior: {ex.Message}");
            }
        }
    }
}
