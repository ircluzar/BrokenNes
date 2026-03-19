using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.JSInterop;
using BrokenNes.Models;
using NesEmulator;
using NesEmulator.Shaders;

namespace BrokenNes.Services;

public class GameSaveService
{
    private const string StorageKey = "game_save_v1";
    private const string LegacyStorageKey = "brokenNesGameSave";
    private readonly IJSRuntime _js;
    private readonly IShaderProvider _shaderProvider;
    private readonly HttpClient _http;
    private static readonly string[] DefaultUnlockedWebmodules = new[]
    {
        "Home", "Continue", "DeckBuilder", "Cores", "Options", "Story", "RomManager", "HexEditor"
    };
    private static readonly string[] DefaultUnlockedBackgrounds = new[] { "Gradient (Default)", "None (Black)" };
    private static readonly string[] DefaultUnlockedNullProviders = new[] { "Static", "Void" };
    private static readonly string[] ProgressionMilestoneWebmodules = new[] { "GlitchHarvester", "TimeJump", "CorruptionSlop", "ImagineBug" };

    public GameSaveService(IJSRuntime js, IShaderProvider shaderProvider, HttpClient http)
    {
        _js = js;
        _shaderProvider = shaderProvider;
        _http = http;
    }

    public async Task<GameSave> LoadAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("nesInterop.idbGetItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var loaded = JsonSerializer.Deserialize<GameSave>(json, opts);
                if (loaded != null)
                {
                    NormalizeContinueSlots(loaded);
                    if (loaded.Level < 1) loaded.Level = 1;
                    if (loaded.Achievements == null) loaded.Achievements = new();
                    // Back-compat default for LevelCleared if missing in older saves
                    _ = loaded.LevelCleared;
                    // Ensure lists exist after deserialization
                    loaded.OwnedCpuIds ??= new();
                    loaded.OwnedPpuIds ??= new();
                    loaded.OwnedApuIds ??= new();
                    loaded.OwnedClockIds ??= new();
                    loaded.OwnedShaderIds ??= new();
                    // Preferred selections (back-compat defaults if missing)
                    loaded.PreferredCpuId ??= "FMC";
                    loaded.PreferredPpuId ??= "FMC";
                    loaded.PreferredApuId ??= "FMC";
                    loaded.PreferredShaderId ??= "PX";
                    loaded.UnlockedWebmodules = NormalizeStringList(loaded.UnlockedWebmodules, DefaultUnlockedWebmodules);
                    loaded.UnlockedBackgrounds = NormalizeStringList(loaded.UnlockedBackgrounds, DefaultUnlockedBackgrounds, NormalizeBackgroundId);
                    loaded.UnlockedNullProviders = NormalizeStringList(loaded.UnlockedNullProviders, DefaultUnlockedNullProviders);
                    loaded.PreferredBackgroundId = NormalizePreferredValue(loaded.PreferredBackgroundId, loaded.UnlockedBackgrounds, "Gradient (Default)", NormalizeBackgroundId);
                    loaded.PreferredNullProviderId = NormalizePreferredValue(loaded.PreferredNullProviderId, loaded.UnlockedNullProviders, "Static");
                    loaded.PendingUnlocks ??= new();
                    NormalizePendingUnlocks(loaded.PendingUnlocks);
                    // Ensure unlock flags are present (backward compatibility defaults)
                    // Keep them off by default to respect progression; options can unlock.
                    // Note: when adding more flags in future, guard similarly.
                    _ = loaded.SavestatesUnlocked;
                    _ = loaded.RtcUnlocked;
                    _ = loaded.GhUnlocked;
                    _ = loaded.ImagineUnlocked;
                    _ = loaded.DebugUnlocked;
                    _ = loaded.SeenStory;
                    // One-time acknowledgement flags (back-compat defaults)
                    _ = loaded.UnderConstructionAcknowledged;
                    // One-time all-cores congrats flag (back-compat default)
                    _ = loaded.AllCoresUnlockedCongrats;
                    // Back-compat default for ROM masquerades mapping
                    loaded.MasqueradeRomToGameId ??= new();
                    // New trusted continue fields (back-compat defaults)
                    _ = loaded.PendingDeckContinue;
                    // Leave rom/title null if not set; timestamp optional
                    return loaded;
                }
            }
        }
        catch { }
        return CreateDefaultSave();
    }

    public async Task SaveAsync(GameSave save)
    {
        NormalizeContinueSlots(save);
        if (save.Level < 1) save.Level = 1;
        save.Achievements ??= new();
    // Persist LevelCleared as-is
    _ = save.LevelCleared;
        save.OwnedCpuIds ??= new();
        save.OwnedPpuIds ??= new();
        save.OwnedApuIds ??= new();
        save.OwnedClockIds ??= new();
        save.OwnedShaderIds ??= new();
    save.MasqueradeRomToGameId ??= new();
    save.UnlockedWebmodules = NormalizeStringList(save.UnlockedWebmodules, DefaultUnlockedWebmodules);
    save.UnlockedBackgrounds = NormalizeStringList(save.UnlockedBackgrounds, DefaultUnlockedBackgrounds, NormalizeBackgroundId);
    save.UnlockedNullProviders = NormalizeStringList(save.UnlockedNullProviders, DefaultUnlockedNullProviders);
    save.PreferredBackgroundId = NormalizePreferredValue(save.PreferredBackgroundId, save.UnlockedBackgrounds, "Gradient (Default)", NormalizeBackgroundId);
    save.PreferredNullProviderId = NormalizePreferredValue(save.PreferredNullProviderId, save.UnlockedNullProviders, "Static");
    save.PendingUnlocks ??= new();
    NormalizePendingUnlocks(save.PendingUnlocks);
    // Trusted continue fields are optional; keep as-is
    // Unlock flags already default to false if missing
    // One-time flags are persisted as-is
    _ = save.UnderConstructionAcknowledged;
    _ = save.AllCoresUnlockedCongrats;
        try
        {
            var json = JsonSerializer.Serialize(save);
            await _js.InvokeVoidAsync("nesInterop.idbSetItem", StorageKey, json);
        }
        catch { }
    }

    private GameSave CreateDefaultSave()
    {
        // Default save contains only FMC cores and PX shader, achievements empty, level 1.
        var gs = new GameSave
        {
            Level = 1,
            LevelCleared = false,
            Achievements = new(),
            SavestatesUnlocked = false,
            RtcUnlocked = false,
            GhUnlocked = false,
            ImagineUnlocked = false,
            DebugUnlocked = false,
            SeenStory = false,
            OwnedCpuIds = new() { "FMC" },
            OwnedPpuIds = new() { "FMC" },
            OwnedApuIds = new() { "FMC" },
            OwnedClockIds = new() { "FMC" },
            OwnedShaderIds = new() { "PX" },
            PreferredCpuId = "FMC",
            PreferredPpuId = "FMC",
            PreferredApuId = "FMC",
            PreferredShaderId = "PX",
            UnlockedWebmodules = DefaultUnlockedWebmodules.ToList(),
            UnlockedBackgrounds = DefaultUnlockedBackgrounds.ToList(),
            UnlockedNullProviders = DefaultUnlockedNullProviders.ToList(),
            PreferredBackgroundId = "Gradient (Default)",
            PreferredNullProviderId = "Static",
            PendingUnlocks = new()
        };
        return gs;
    }

    private static List<string> NormalizeStringList(IEnumerable<string>? values, IEnumerable<string>? defaults, Func<string, string>? normalizer = null)
    {
        var result = new List<string>();

        void AddRange(IEnumerable<string>? source)
        {
            if (source == null)
            {
                return;
            }

            foreach (var value in source)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalizedValue = normalizer != null ? normalizer(value) : value.Trim();
                if (string.IsNullOrWhiteSpace(normalizedValue))
                {
                    continue;
                }

                if (!result.Any(existing => existing.Equals(normalizedValue, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(normalizedValue);
                }
            }
        }

        AddRange(defaults);
        AddRange(values);
        return result;
    }

    private static string NormalizePreferredValue(string? preferredValue, IReadOnlyCollection<string> unlockedValues, string fallback, Func<string, string>? normalizer = null)
    {
        var normalizedPreferredValue = string.IsNullOrWhiteSpace(preferredValue)
            ? null
            : normalizer != null ? normalizer(preferredValue) : preferredValue.Trim();
        var normalizedFallback = normalizer != null ? normalizer(fallback) : fallback;

        if (!string.IsNullOrWhiteSpace(normalizedPreferredValue) && unlockedValues.Any(value => value.Equals(normalizedPreferredValue, StringComparison.OrdinalIgnoreCase)))
        {
            return unlockedValues.First(value => value.Equals(normalizedPreferredValue, StringComparison.OrdinalIgnoreCase));
        }

        if (unlockedValues.Any(value => value.Equals(normalizedFallback, StringComparison.OrdinalIgnoreCase)))
        {
            return unlockedValues.First(value => value.Equals(normalizedFallback, StringComparison.OrdinalIgnoreCase));
        }

        return unlockedValues.FirstOrDefault() ?? normalizedFallback;
    }

    private static string NormalizeBackgroundId(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
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

    private static void NormalizePendingUnlocks(List<PendingUnlockBundle> pendingUnlocks)
    {
        foreach (var bundle in pendingUnlocks)
        {
            bundle.Id = string.IsNullOrWhiteSpace(bundle.Id) ? Guid.NewGuid().ToString("N") : bundle.Id;
            bundle.Source ??= string.Empty;
            bundle.Items ??= new();

            foreach (var item in bundle.Items)
            {
                item.Id = item.Id?.Trim() ?? string.Empty;
                item.Type = item.Type?.Trim() ?? string.Empty;
            }
        }
    }

    public async Task ClearDeckBuilderSaveAsync()
    {
        try
        {
            await _http.PostAsync("/api/save/reset", content: null);
        }
        catch { }

        try { await _js.InvokeVoidAsync("nesInterop.idbRemoveItem", StorageKey); } catch { }
        try { await _js.InvokeVoidAsync("eval", $"try{{localStorage.removeItem('{StorageKey}');localStorage.removeItem('{LegacyStorageKey}');}}catch{{}}"); } catch { }

        // Reset achievements and owned cores to default set (FMC + PX)
        var save = CreateDefaultSave();
        await SaveAsync(save);
    }

    public async Task UnlockAllCoresAsync()
    {
        // Achievements are not affected; we only update owned core ids.
        var save = await LoadAsync();
        try { save.OwnedCpuIds = CoreRegistry.CpuIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new(); } catch { save.OwnedCpuIds = new(); }
        try { save.OwnedPpuIds = CoreRegistry.PpuIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new(); } catch { save.OwnedPpuIds = new(); }
        try { save.OwnedApuIds = CoreRegistry.ApuIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new(); } catch { save.OwnedApuIds = new(); }
        try { save.OwnedClockIds = ClockRegistry.Ids?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new(); } catch { save.OwnedClockIds = new(); }
        try { save.OwnedShaderIds = _shaderProvider.All?.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new(); } catch { save.OwnedShaderIds = new(); }

        // Log in web console for visibility
        try
        {
            var parts = new List<string>();
            try { parts.AddRange((save.OwnedCpuIds ?? new()).Select(id => $"CPU_{id}")); } catch { }
            try { parts.AddRange((save.OwnedPpuIds ?? new()).Select(id => $"PPU_{id}")); } catch { }
            try { parts.AddRange((save.OwnedApuIds ?? new()).Select(id => $"APU_{id}")); } catch { }
            try { parts.AddRange((save.OwnedClockIds ?? new()).Select(id => $"CLOCK_{id}")); } catch { }
            try { parts.AddRange((save.OwnedShaderIds ?? new()).Select(id => $"SHADER_{id}")); } catch { }
            var labels = string.Join(", ", parts);
            var js = $"try{{console.log('Unlocked cores: {labels}');}}catch(e){{}}";
            await _js.InvokeVoidAsync("eval", js);
        }
        catch { }
        await SaveAsync(save);
    }

    // Feature unlock helpers (used from Options and game flow when earned)
    public async Task UnlockSavestatesAsync()
    {
        var save = await LoadAsync();
        save.SavestatesUnlocked = true;
        AddUnlockedWebmodule(save, "TimeJump");
        await SaveAsync(save);
    }

    public async Task UnlockRtcAsync()
    {
        var save = await LoadAsync();
        save.RtcUnlocked = true;
        AddUnlockedWebmodule(save, "GlitchHarvester");
        await SaveAsync(save);
    }

    public async Task UnlockGhAsync()
    {
        var save = await LoadAsync();
        save.GhUnlocked = true;
        AddUnlockedWebmodule(save, "GlitchHarvester");
        await SaveAsync(save);
    }

    public async Task UnlockImagineAsync()
    {
        var save = await LoadAsync();
        save.ImagineUnlocked = true;
        AddUnlockedWebmodule(save, "ImagineBug");
        await SaveAsync(save);
    }

    public async Task UnlockAllFeaturesAsync()
    {
        var save = await LoadAsync();
        save.SavestatesUnlocked = true;
        save.RtcUnlocked = true;
        save.GhUnlocked = true;
        save.ImagineUnlocked = true;
        foreach (var moduleId in ProgressionMilestoneWebmodules)
        {
            AddUnlockedWebmodule(save, moduleId);
        }
        await SaveAsync(save);
    }

    public async Task UnlockDebugAsync()
    {
        var save = await LoadAsync();
        save.DebugUnlocked = true;
        await SaveAsync(save);
    }

    private static void AddUnlockedWebmodule(GameSave save, string moduleId)
    {
        save.UnlockedWebmodules ??= new();
        if (!save.UnlockedWebmodules.Any(existing => existing.Equals(moduleId, StringComparison.OrdinalIgnoreCase)))
        {
            save.UnlockedWebmodules.Add(moduleId);
        }
    }

    // Trusted DeckBuilder Continue helpers
    public async Task SetPendingDeckContinueAsync(string romKey, string? title)
        => await SetPendingDeckContinueAsync(romKey, title, null);

    public async Task SetPendingDeckContinueAsync(string romKey, string? title, string? previewImagePath)
    {
        try
        {
            var normalizedRomKey = NormalizeContinueSlotKey(romKey);
            if (string.IsNullOrWhiteSpace(normalizedRomKey))
            {
                return;
            }

            var save = await LoadAsync();
            NormalizeContinueSlots(save);
            var slot = new ContinueStateSlot
            {
                RomKey = romKey,
                Title = string.IsNullOrWhiteSpace(title) ? romKey : title,
                UpdatedAtUtc = DateTime.UtcNow,
                PreviewImagePath = string.IsNullOrWhiteSpace(previewImagePath) ? null : previewImagePath
            };
            save.ContinueSlots[normalizedRomKey] = slot;
            save.PendingDeckContinue = true;
            save.PendingDeckContinueRom = romKey;
            save.PendingDeckContinueTitle = string.IsNullOrWhiteSpace(title) ? romKey : title;
            save.PendingDeckContinueAtUtc = slot.UpdatedAtUtc;
            await SaveAsync(save);
        }
        catch { }
    }

    public async Task ClearPendingDeckContinueAsync()
        => await ClearPendingDeckContinueAsync(null);

    public async Task ClearPendingDeckContinueAsync(string? romKey)
    {
        try
        {
            var save = await LoadAsync();
            NormalizeContinueSlots(save);
            var normalizedRomKey = NormalizeContinueSlotKey(romKey);
            var changed = false;

            if (!string.IsNullOrWhiteSpace(normalizedRomKey))
            {
                changed = save.ContinueSlots.Remove(normalizedRomKey);
            }
            else if (save.PendingDeckContinue || !string.IsNullOrWhiteSpace(save.PendingDeckContinueRom) || !string.IsNullOrWhiteSpace(save.PendingDeckContinueTitle) || save.ContinueSlots.Count > 0)
            {
                changed = save.ContinueSlots.Count > 0;
                save.ContinueSlots.Clear();
            }

            if (!changed && string.IsNullOrWhiteSpace(normalizedRomKey))
            {
                if (!save.PendingDeckContinue && string.IsNullOrWhiteSpace(save.PendingDeckContinueRom) && string.IsNullOrWhiteSpace(save.PendingDeckContinueTitle))
                {
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(normalizedRomKey)
                && string.Equals(NormalizeContinueSlotKey(save.PendingDeckContinueRom), normalizedRomKey, StringComparison.Ordinal))
            {
                var latest = GetLatestContinueSlot(save);
                ApplyLegacyPendingDeckContinue(save, latest);
                changed = true;
            }
            else if (string.IsNullOrWhiteSpace(normalizedRomKey))
            {
                save.PendingDeckContinue = false;
                save.PendingDeckContinueRom = null;
                save.PendingDeckContinueTitle = null;
                save.PendingDeckContinueAtUtc = null;
                changed = true;
            }

            if (changed)
            {
                await SaveAsync(save);
            }
        }
        catch { }
    }

    private static void NormalizeContinueSlots(GameSave save)
    {
        save.ContinueSlots ??= new();

        if (save.ContinueSlots.Count > 0)
        {
            var normalizedSlots = new Dictionary<string, ContinueStateSlot>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in save.ContinueSlots)
            {
                var normalizedKey = NormalizeContinueSlotKey(entry.Key);
                if (string.IsNullOrWhiteSpace(normalizedKey))
                {
                    normalizedKey = NormalizeContinueSlotKey(entry.Value?.RomKey);
                }

                if (string.IsNullOrWhiteSpace(normalizedKey))
                {
                    continue;
                }

                var slot = entry.Value ?? new ContinueStateSlot();
                slot.RomKey = string.IsNullOrWhiteSpace(slot.RomKey) ? entry.Key : slot.RomKey;
                slot.Title = string.IsNullOrWhiteSpace(slot.Title) ? slot.RomKey : slot.Title;
                normalizedSlots[normalizedKey] = slot;
            }

            save.ContinueSlots = normalizedSlots;
        }

        if (save.ContinueSlots.Count == 0 && save.PendingDeckContinue && !string.IsNullOrWhiteSpace(save.PendingDeckContinueRom))
        {
            var normalizedKey = NormalizeContinueSlotKey(save.PendingDeckContinueRom);
            if (!string.IsNullOrWhiteSpace(normalizedKey))
            {
                save.ContinueSlots[normalizedKey] = new ContinueStateSlot
                {
                    RomKey = save.PendingDeckContinueRom!,
                    Title = string.IsNullOrWhiteSpace(save.PendingDeckContinueTitle) ? save.PendingDeckContinueRom : save.PendingDeckContinueTitle,
                    UpdatedAtUtc = save.PendingDeckContinueAtUtc
                };
            }
        }

        var latestSlot = GetLatestContinueSlot(save);
        ApplyLegacyPendingDeckContinue(save, latestSlot);
    }

    private static void ApplyLegacyPendingDeckContinue(GameSave save, ContinueStateSlot? slot)
    {
        if (slot == null)
        {
            save.PendingDeckContinue = false;
            save.PendingDeckContinueRom = null;
            save.PendingDeckContinueTitle = null;
            save.PendingDeckContinueAtUtc = null;
            return;
        }

        save.PendingDeckContinue = true;
        save.PendingDeckContinueRom = slot.RomKey;
        save.PendingDeckContinueTitle = string.IsNullOrWhiteSpace(slot.Title) ? slot.RomKey : slot.Title;
        save.PendingDeckContinueAtUtc = slot.UpdatedAtUtc;
    }

    private static ContinueStateSlot? GetLatestContinueSlot(GameSave save)
    {
        return save.ContinueSlots.Values
            .OrderByDescending(slot => slot.UpdatedAtUtc ?? DateTime.MinValue)
            .ThenBy(slot => slot.RomKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string NormalizeContinueSlotKey(string? romKey)
    {
        return string.IsNullOrWhiteSpace(romKey)
            ? string.Empty
            : romKey.Trim().ToLowerInvariant();
    }

    // Count cores the player owns across all categories.
    public int GetOwnedCoresCount(GameSave? save = null)
    {
        try
        {
            save ??= CreateDefaultSave();
            return (save.OwnedCpuIds?.Count ?? 0)
                 + (save.OwnedPpuIds?.Count ?? 0)
                 + (save.OwnedApuIds?.Count ?? 0)
                 + (save.OwnedClockIds?.Count ?? 0)
                 + (save.OwnedShaderIds?.Count ?? 0);
        }
        catch { return 0; }
    }

    // Total number of discoverable cores across all categories.
    public int GetTotalCoresCount()
    {
        try
        {
            var cpu = CoreRegistry.CpuIds?.Count ?? 0;
            var ppu = CoreRegistry.PpuIds?.Count ?? 0;
            var apu = CoreRegistry.ApuIds?.Count ?? 0;
            var clocks = 0; try { clocks = ClockRegistry.Ids?.Count ?? 0; } catch { clocks = 0; }
            var shaders = 0; try { shaders = _shaderProvider?.All?.Count ?? 0; } catch { shaders = 0; }
            return cpu + ppu + apu + clocks + shaders;
        }
        catch { return 0; }
    }
}
