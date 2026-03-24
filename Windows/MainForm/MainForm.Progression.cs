using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BrokenNes.Models;
using BrokenNes.Windows.Rendering;
using NesEmulator;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        private static string NormalizeBackgroundId(string? backgroundName)
        {
            var trimmed = backgroundName?.Trim() ?? string.Empty;
            if (trimmed.Equals("Gradient", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Gradient (Default)", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("StaticGradient", StringComparison.OrdinalIgnoreCase))
            {
                return "Gradient (Default)";
            }

            if (trimmed.Equals("Black", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("None", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("None (Black)", StringComparison.OrdinalIgnoreCase))
            {
                return "None (Black)";
            }

            return trimmed;
        }

        private GameSave LoadProgressionSnapshot()
        {
            try
            {
                return progressionSaveService.LoadAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Progression] Failed to load progression snapshot: {ex.Message}");
                return new GameSave();
            }
        }

        private bool IsWebModuleUnlocked(WebModuleInfo module, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            var targetModuleId = !string.IsNullOrWhiteSpace(module.Config.LoadModule)
                ? module.Config.LoadModule.Trim()
                : module.FolderName;
            return progressionSaveService.IsWebmoduleUnlocked(save, targetModuleId);
        }

        private bool IsBackgroundUnlocked(string backgroundName, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            return progressionSaveService.IsBackgroundUnlocked(save, NormalizeBackgroundId(backgroundName));
        }

        private bool IsNullProviderUnlocked(string providerName, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            return progressionSaveService.IsNullProviderUnlocked(save, providerName);
        }

        private static bool IsOwnedCore(IEnumerable<string>? ownedIds, string coreId)
        {
            return ownedIds?.Any(value => value.Equals(coreId, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private bool IsCpuCoreUnlocked(string coreId, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            return IsOwnedCore(save.OwnedCpuIds, coreId);
        }

        private bool IsPpuCoreUnlocked(string coreId, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            return IsOwnedCore(save.OwnedPpuIds, coreId);
        }

        private bool IsApuCoreUnlocked(string coreId, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            return IsOwnedCore(save.OwnedApuIds, coreId);
        }

        private bool IsShaderUnlocked(string shaderId, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            return IsOwnedCore(save.OwnedShaderIds, shaderId);
        }

        private static string ResolveUnlockedCoreSelection(string? selectedCoreId, IEnumerable<string> availableCoreIds, IEnumerable<string>? ownedCoreIds, string fallbackCoreId)
        {
            var available = availableCoreIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var owned = (ownedCoreIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(selectedCoreId)
                && available.Any(value => value.Equals(selectedCoreId, StringComparison.OrdinalIgnoreCase))
                && owned.Any(value => value.Equals(selectedCoreId, StringComparison.OrdinalIgnoreCase)))
            {
                return available.First(value => value.Equals(selectedCoreId, StringComparison.OrdinalIgnoreCase));
            }

            if (available.Any(value => value.Equals(fallbackCoreId, StringComparison.OrdinalIgnoreCase))
                && owned.Any(value => value.Equals(fallbackCoreId, StringComparison.OrdinalIgnoreCase)))
            {
                return available.First(value => value.Equals(fallbackCoreId, StringComparison.OrdinalIgnoreCase));
            }

            return available.FirstOrDefault(value => owned.Any(ownedId => ownedId.Equals(value, StringComparison.OrdinalIgnoreCase)))
                ?? fallbackCoreId;
        }

        private static string ResolveUnlockedShaderSelection(string? selectedShaderId, IEnumerable<string> availableShaderIds, IEnumerable<string>? ownedShaderIds, string fallbackShaderId)
        {
            return ResolveUnlockedCoreSelection(selectedShaderId, availableShaderIds, ownedShaderIds, fallbackShaderId);
        }

        private bool IsRtcStackUnlocked(GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            return progressionSaveService.IsWebmoduleUnlocked(save, "GlitchHarvester");
        }

        private bool IsTimeJumpUnlocked(GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            return progressionSaveService.IsWebmoduleUnlocked(save, "TimeJump");
        }

        private bool IsImagineBugUnlocked(GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            return progressionSaveService.IsWebmoduleUnlocked(save, "ImagineBug");
        }

        private string ResolveUnlockedBlastType(string? blastType, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            if (!IsRtcStackUnlocked(save))
            {
                return "RANDOM";
            }

            var normalized = (blastType ?? "RANDOM").Trim().ToUpperInvariant();
            return normalized switch
            {
                "RANDOM" or "TILT" or "RANDOMTILT" or "NOP" or "BITFLIP" => normalized,
                "IMAGINE_NEXT" or "IMAGINE_RANDOM" when IsImagineBugUnlocked(save) => normalized,
                _ => "RANDOM"
            };
        }

        private string ResolveUnlockedCrashBehavior(string? crashBehavior, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            var normalized = crashBehavior?.Trim() ?? string.Empty;
            if (normalized.Equals("RedScreen", StringComparison.OrdinalIgnoreCase))
            {
                return "RedScreen";
            }

            if (normalized.Equals("ImagineFix", StringComparison.OrdinalIgnoreCase))
            {
                return IsImagineBugUnlocked(save) ? "ImagineFix" : "IgnoreErrors";
            }

            return "IgnoreErrors";
        }

        private void EnsureUnlockedProgressionCapabilities(GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();

            var safeCrashBehavior = ResolveUnlockedCrashBehavior(config.CrashBehavior, save);
            if (!string.Equals(config.CrashBehavior, safeCrashBehavior, StringComparison.OrdinalIgnoreCase))
            {
                Helpers.ConfigHelper.Update(config, c => c.CrashBehavior = safeCrashBehavior);
            }

            var rtcUnlocked = IsRtcStackUnlocked(save);
            var imagineUnlocked = IsImagineBugUnlocked(save);
            var safeBlastType = ResolveUnlockedBlastType(corruptor.BlastType, save);
            var stateChanged = false;

            lock (corruptorLock)
            {
                if (!string.Equals(corruptor.BlastType, safeBlastType, StringComparison.OrdinalIgnoreCase))
                {
                    corruptor.BlastType = safeBlastType;
                    stateChanged = true;
                }

                if (!rtcUnlocked && corruptor.AutoCorrupt)
                {
                    corruptor.AutoCorrupt = false;
                    corruptor.LastBlastInfo = "Auto-corrupt unavailable until RTC + Glitch Harvester unlocks.";
                    stateChanged = true;
                }

                if (!imagineUnlocked && corruptor.StubbornMode)
                {
                    corruptor.StubbornMode = false;
                    stateChanged = true;
                }
            }

            if (stateChanged)
            {
                NotifyCorruptorChanged();
            }
        }

        private string ResolveUnlockedBackgroundSelection(string? selectedBackground, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            var available = BrokenNes.Windows.Rendering.NesDirectXRenderer.GetAvailableBackgrounds()
                .Where(name => !string.IsNullOrWhiteSpace(name) && name != "---")
                .Select(NormalizeBackgroundId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var preferred = NormalizeBackgroundId(selectedBackground);
            if (!string.IsNullOrWhiteSpace(preferred)
                && available.Any(name => name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                && progressionSaveService.IsBackgroundUnlocked(save, preferred))
            {
                return preferred;
            }

            var unlocked = available.FirstOrDefault(name => progressionSaveService.IsBackgroundUnlocked(save, name));
            return unlocked ?? "Gradient (Default)";
        }

        private string ResolveUnlockedNullProviderSelection(string? selectedNullProvider, GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            var available = NesEmulator.NES.GetAvailableNullProviders().ToList();

            if (!string.IsNullOrWhiteSpace(selectedNullProvider)
                && available.Any(name => name.Equals(selectedNullProvider, StringComparison.OrdinalIgnoreCase))
                && progressionSaveService.IsNullProviderUnlocked(save, selectedNullProvider))
            {
                return selectedNullProvider;
            }

            var unlocked = available.FirstOrDefault(name => progressionSaveService.IsNullProviderUnlocked(save, name));
            return unlocked ?? "Static";
        }

        private void EnsureUnlockedProgressionSelections(GameSave? save = null)
        {
            save ??= LoadProgressionSnapshot();
            EnsureUnlockedProgressionCapabilities(save);

            var safeCpuCore = ResolveUnlockedCoreSelection(config.SelectedCpuCore, CoreRegistry.CpuIds, save.OwnedCpuIds, "FMC");
            if (!string.Equals(config.SelectedCpuCore, safeCpuCore, StringComparison.OrdinalIgnoreCase))
            {
                Helpers.ConfigHelper.Update(config, c => c.SelectedCpuCore = safeCpuCore);
            }

            var safePpuCore = ResolveUnlockedCoreSelection(config.SelectedPpuCore, CoreRegistry.PpuIds, save.OwnedPpuIds, "FMC");
            if (!string.Equals(config.SelectedPpuCore, safePpuCore, StringComparison.OrdinalIgnoreCase))
            {
                Helpers.ConfigHelper.Update(config, c => c.SelectedPpuCore = safePpuCore);
            }

            var safeApuCore = ResolveUnlockedCoreSelection(config.SelectedApuCore, CoreRegistry.ApuIds, save.OwnedApuIds, "FMC");
            if (!string.Equals(config.SelectedApuCore, safeApuCore, StringComparison.OrdinalIgnoreCase))
            {
                Helpers.ConfigHelper.Update(config, c => c.SelectedApuCore = safeApuCore);
            }

            var safeShader = ResolveUnlockedShaderSelection(config.CurrentShader, NesDirectXRenderer.GetAvailableShaders(), save.OwnedShaderIds, "PX");
            if (!string.Equals(config.CurrentShader, safeShader, StringComparison.OrdinalIgnoreCase))
            {
                Helpers.ConfigHelper.Update(config, c => c.CurrentShader = safeShader);
            }

            var safeBackground = ResolveUnlockedBackgroundSelection(config.SelectedBackground, save);
            if (!string.Equals(config.SelectedBackground, safeBackground, StringComparison.OrdinalIgnoreCase))
            {
                Helpers.ConfigHelper.Update(config, c => c.SelectedBackground = safeBackground);
            }

            var safeNullProvider = ResolveUnlockedNullProviderSelection(config.SelectedNullProvider, save);
            if (!string.Equals(config.SelectedNullProvider, safeNullProvider, StringComparison.OrdinalIgnoreCase))
            {
                Helpers.ConfigHelper.Update(config, c => c.SelectedNullProvider = safeNullProvider);
            }
        }

        private static string GetNullProviderDisplayText(string providerName)
        {
            return providerName.Equals("Static", StringComparison.OrdinalIgnoreCase)
                ? "Static (Default)"
                : providerName.Equals("Void", StringComparison.OrdinalIgnoreCase)
                    ? "Void (Black)"
                    : providerName;
        }

        private void RebuildBackgroundMenu(GameSave? save = null)
        {
            if (backgroundMenu == null)
            {
                return;
            }

            save ??= LoadProgressionSnapshot();
            EnsureUnlockedProgressionSelections(save);

            backgroundMenu.DropDownItems.Clear();
            foreach (var backgroundName in BrokenNes.Windows.Rendering.NesDirectXRenderer.GetAvailableBackgrounds())
            {
                if (backgroundName == "---")
                {
                    backgroundMenu.DropDownItems.Add(new ToolStripSeparator());
                    continue;
                }

                var normalizedName = NormalizeBackgroundId(backgroundName);
                var unlocked = progressionSaveService.IsBackgroundUnlocked(save, normalizedName);
                var menuItem = new ToolStripMenuItem(unlocked ? normalizedName : $"{normalizedName} [Locked]", null, (s, e) => SetBackground(normalizedName))
                {
                    Tag = normalizedName,
                    Enabled = unlocked,
                    Checked = normalizedName.Equals(config.SelectedBackground, StringComparison.OrdinalIgnoreCase)
                };
                backgroundMenu.DropDownItems.Add(menuItem);
            }
        }

        private void RebuildNullProviderMenu(GameSave? save = null)
        {
            if (nullProviderMenu == null)
            {
                return;
            }

            save ??= LoadProgressionSnapshot();
            EnsureUnlockedProgressionSelections(save);

            nullProviderMenu.DropDownItems.Clear();
            var available = NesEmulator.NES.GetAvailableNullProviders().ToList();
            var defaultProviders = new[] { "Static", "Void" };

            foreach (var providerName in defaultProviders)
            {
                if (!available.Any(p => p.Equals(providerName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var unlocked = progressionSaveService.IsNullProviderUnlocked(save, providerName);
                var menuItem = new ToolStripMenuItem(
                    unlocked ? GetNullProviderDisplayText(providerName) : $"{GetNullProviderDisplayText(providerName)} [Locked]",
                    null,
                    (s, e) => SetNullProvider(providerName))
                {
                    Tag = providerName,
                    Enabled = unlocked,
                    Checked = providerName.Equals(config.SelectedNullProvider, StringComparison.OrdinalIgnoreCase)
                };
                nullProviderMenu.DropDownItems.Add(menuItem);
            }

            var otherProviders = available
                .Where(name => !defaultProviders.Any(defaultName => defaultName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (otherProviders.Count > 0)
            {
                nullProviderMenu.DropDownItems.Add(new ToolStripSeparator());
                foreach (var providerName in otherProviders)
                {
                    var unlocked = progressionSaveService.IsNullProviderUnlocked(save, providerName);
                    var menuItem = new ToolStripMenuItem(
                        unlocked ? providerName : $"{providerName} [Locked]",
                        null,
                        (s, e) => SetNullProvider(providerName))
                    {
                        Tag = providerName,
                        Enabled = unlocked,
                        Checked = providerName.Equals(config.SelectedNullProvider, StringComparison.OrdinalIgnoreCase)
                    };
                    nullProviderMenu.DropDownItems.Add(menuItem);
                }
            }
        }

        private void RebuildToolsMenu(GameSave? save = null)
        {
            if (toolsMenu == null)
            {
                return;
            }

            save ??= LoadProgressionSnapshot();
            toolsMenu.DropDownItems.Clear();

            var webModules = WebModuleManager.DiscoverModules();
            var toolWebModules = webModules.Where(m => m.Config.ShowInToolsMenu).ToArray();
            var activities = toolWebModules
                .Where(m => m.Config.IsActivity)
                .OrderBy(m => m.FolderName.Equals("DeckBuilder", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var tools = toolWebModules.Where(m => !m.Config.IsActivity).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();

            void AddModuleItems(IEnumerable<WebModuleInfo> modules)
            {
                foreach (var module in modules)
                {
                    var moduleToLoad = module;
                    if (!string.IsNullOrWhiteSpace(module.Config.LoadModule))
                    {
                        var targetModule = webModules.FirstOrDefault(m => m.FolderName.Equals(module.Config.LoadModule, StringComparison.OrdinalIgnoreCase));
                        if (targetModule != null)
                        {
                            moduleToLoad = targetModule;
                        }
                    }

                    var unlocked = IsWebModuleUnlocked(moduleToLoad, save);
                    var moduleItem = new ToolStripMenuItem(unlocked ? module.Name : $"{module.Name} [Locked]", null, (s, e) => LoadWebModule(moduleToLoad))
                    {
                        Tag = moduleToLoad.FolderName,
                        Enabled = unlocked
                    };
                    toolsMenu.DropDownItems.Add(moduleItem);
                }
            }

            AddModuleItems(activities);
            if (activities.Length > 0 && tools.Length > 0)
            {
                toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            }
            AddModuleItems(tools);

            if (currentToolOrActivityModule != null)
            {
                if (toolsMenu.DropDownItems.Count > 0)
                {
                    toolsMenu.DropDownItems.Add(new ToolStripSeparator { Tag = "ExitModule" });
                }

                var exitItem = new ToolStripMenuItem($"Exit {currentToolOrActivityModule.Name}", null, (sender, args) => SwitchViewMode(ViewMode.Emulator))
                {
                    Font = new Font(toolsMenu.Font, FontStyle.Bold),
                    Tag = "ExitModule"
                };
                toolsMenu.DropDownItems.Add(exitItem);
            }
        }

        private void RebuildWebModulesMenu(GameSave? save = null)
        {
            if (webModulesMenu == null)
            {
                return;
            }

            save ??= LoadProgressionSnapshot();
            webModulesMenu.DropDownItems.Clear();

            var emulatorModeItem = new ToolStripMenuItem("Emulator Mode", null, (s, e) => SwitchViewMode(ViewMode.Emulator))
            {
                ShortcutKeys = Keys.Control | Keys.D1
            };
            webModulesMenu.DropDownItems.Add(emulatorModeItem);

            var widgetModeItem = new ToolStripMenuItem("Widget Mode", null, (s, e) => SwitchViewMode(ViewMode.Widget))
            {
                ShortcutKeys = Keys.Control | Keys.D2
            };
            webModulesMenu.DropDownItems.Add(widgetModeItem);

            var webModeItem = new ToolStripMenuItem("Web Mode (Test Page)", null, (s, e) =>
            {
                string webmodulesIndexUri = $"https://{WebModuleManager.SharedVirtualHostName}/index.html";
                Helpers.WebViewHelper.NavigateToUri(webView, webmodulesIndexUri);
                SwitchViewMode(ViewMode.Web);
            })
            {
                ShortcutKeys = Keys.Control | Keys.D3
            };
            webModulesMenu.DropDownItems.Add(webModeItem);

            var overlayModeItem = new ToolStripMenuItem("Overlay Mode", null, (s, e) => SwitchViewMode(ViewMode.Overlay))
            {
                ShortcutKeys = Keys.Control | Keys.D4
            };
            webModulesMenu.DropDownItems.Add(overlayModeItem);

            webModulesMenu.DropDownItems.Add(new ToolStripSeparator());

            var webModules = WebModuleManager.DiscoverModules();
            if (webModules.Length == 0)
            {
                webModulesMenu.DropDownItems.Add(new ToolStripMenuItem("(No modules available)") { Enabled = false });
                return;
            }

            foreach (var module in webModules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var unlocked = IsWebModuleUnlocked(module, save);
                var moduleItem = new ToolStripMenuItem(unlocked ? module.Name : $"{module.Name} [Locked]", null, (s, e) => LoadWebModule(module))
                {
                    Tag = module.FolderName,
                    Enabled = unlocked
                };
                webModulesMenu.DropDownItems.Add(moduleItem);
            }
        }

        private void RefreshProgressionUi()
        {
            var save = LoadProgressionSnapshot();
            EnsureUnlockedProgressionSelections(save);

            if (useDirectX && dxRenderer != null)
            {
                dxRenderer.RenderScanlines = config.RenderScanlines;
                dxRenderer.RenderViewportShadow = config.RenderViewportShadow;

                if (!string.IsNullOrWhiteSpace(config.SelectedBackground))
                {
                    dxRenderer.SetBackground(config.SelectedBackground);
                }

                if (!string.IsNullOrWhiteSpace(config.CurrentShader))
                {
                    NesShaderControl.SwitchShader(config.CurrentShader);
                }

                dxRenderer.UseShader = true;
                config.ShadersEnabled = true;
            }

            if (nes != null)
            {
                if (!string.IsNullOrWhiteSpace(config.SelectedNullProvider))
                {
                    nes.SetNullProvider(config.SelectedNullProvider);
                }

                if (!string.IsNullOrWhiteSpace(config.SelectedCpuCore))
                {
                    nes.SetCpuCore(config.SelectedCpuCore);
                }

                if (!string.IsNullOrWhiteSpace(config.SelectedPpuCore))
                {
                    nes.SetPpuCore(config.SelectedPpuCore);
                }

                if (!string.IsNullOrWhiteSpace(config.SelectedApuCore))
                {
                    nes.SetApuCore(config.SelectedApuCore);
                }
            }

            ApplyCrashBehavior();
            RebuildBackgroundMenu(save);
            RebuildNullProviderMenu(save);
            RebuildToolsMenu(save);
            RebuildWebModulesMenu(save);
            UpdateConfigMenus();
            UpdateCoresMenus();
        }
    }
}