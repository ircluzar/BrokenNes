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
        private void SetCpuCore(string coreId)
        {
            if (nes == null) return;
            nes.SetCpuCore(coreId);
            config.SelectedCpuCore = coreId;
            config.Save();
            UpdateCoresMenus(); // Refresh to update checkmarks
        }
        
        private void SetPpuCore(string coreId)
        {
            if (nes == null) return;
            nes.SetPpuCore(coreId);
            config.SelectedPpuCore = coreId;
            config.Save();
            UpdateCoresMenus(); // Refresh to update checkmarks
        }
        
        private void SetApuCore(string coreId)
        {
            if (nes == null) return;
            nes.SetApuCore(coreId);
            config.SelectedApuCore = coreId;
            config.Save();
            UpdateCoresMenus(); // Refresh to update checkmarks
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
                                config.CurrentShader = randomShader;
                                config.Save();
                            }
                        }
                    }
                    break;
            }
        }
        
        private void SetNullProvider(string providerName)
        {
            config.SelectedNullProvider = providerName;
            config.Save();
            
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
            config.CrashBehavior = behavior;
            config.Save();
            
            // Apply to current NES instance if one is running
            ApplyCrashBehavior();
            
            UpdateConfigMenus();
            
            Console.WriteLine($"Crash behavior set to: {behavior}");
        }
        
        private void ApplyCrashBehavior()
        {
            if (nes == null) return;
            
            try
            {
                lock (emulationLock)
                {
                    if (nes != null)
                    {
                        // Sync corruptor's crash behavior with config
                        corruptor.CrashBehavior = config.CrashBehavior;
                        
                        switch (config.CrashBehavior)
                        {
                            case "IgnoreErrors":
                                nes.SetCrashBehavior(NES.CrashBehavior.IgnoreErrors);
                                break;
                            case "ImagineFix":
                                nes.SetCrashBehavior(NES.CrashBehavior.ImagineFix);
                                nes.SetStubbornFixEnabled(corruptor.StubbornMode);
                                break;
                            default: // "RedScreen"
                                nes.SetCrashBehavior(NES.CrashBehavior.RedScreen);
                                break;
                        }
                        if (imagineEngine != null)
                        {
                            nes.ImagineShot = pc =>
                            {
                                try { imagineEngine.ImagineFromPc(pc, Math.Clamp(corruptor.CorruptIntensity, 1, 32)); }
                                catch (Exception ex) { Console.WriteLine($"ImagineShot error: {ex.Message}"); }
                            };
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
