using System.Text.Json;
using Microsoft.JSInterop;
using BrokenNes.Models;
using NesEmulator;
using NesEmulator.Shaders;

namespace BrokenNes.Services;

public class GameSaveService
{
    private const string StorageKey = "game_save_v1";
    private readonly IJSRuntime _js;
    private readonly IShaderProvider _shaderProvider;

    public GameSaveService(IJSRuntime js, IShaderProvider shaderProvider)
    {
        _js = js;
        _shaderProvider = shaderProvider;
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
                    // Ensure unlock flags are present (backward compatibility defaults)
                    // Keep them off by default to respect progression; options can unlock.
                    // Note: when adding more flags in future, guard similarly.
                    _ = loaded.SavestatesUnlocked;
                    _ = loaded.RtcUnlocked;
                    _ = loaded.GhUnlocked;
                    _ = loaded.ImagineUnlocked;
                    _ = loaded.DebugUnlocked;
                    _ = loaded.SeenStory;
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
        if (save.Level < 1) save.Level = 1;
        save.Achievements ??= new();
    // Persist LevelCleared as-is
    _ = save.LevelCleared;
        save.OwnedCpuIds ??= new();
        save.OwnedPpuIds ??= new();
        save.OwnedApuIds ??= new();
        save.OwnedClockIds ??= new();
        save.OwnedShaderIds ??= new();
    // Trusted continue fields are optional; keep as-is
    // Unlock flags already default to false if missing
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
            OwnedShaderIds = new() { "PX" }
        };
        return gs;
    }

    public async Task ClearDeckBuilderSaveAsync()
    {
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
        await SaveAsync(save);
    }

    // Feature unlock helpers (used from Options and game flow when earned)
    public async Task UnlockSavestatesAsync()
    {
        var save = await LoadAsync();
        save.SavestatesUnlocked = true;
        await SaveAsync(save);
    }

    public async Task UnlockRtcAsync()
    {
        var save = await LoadAsync();
        save.RtcUnlocked = true;
        await SaveAsync(save);
    }

    public async Task UnlockGhAsync()
    {
        var save = await LoadAsync();
        save.GhUnlocked = true;
        await SaveAsync(save);
    }

    public async Task UnlockImagineAsync()
    {
        var save = await LoadAsync();
        save.ImagineUnlocked = true;
        await SaveAsync(save);
    }

    public async Task UnlockAllFeaturesAsync()
    {
        var save = await LoadAsync();
    save.SavestatesUnlocked = true;
        save.RtcUnlocked = true;
        save.GhUnlocked = true;
        save.ImagineUnlocked = true;
        await SaveAsync(save);
    }

    public async Task UnlockDebugAsync()
    {
        var save = await LoadAsync();
        save.DebugUnlocked = true;
        await SaveAsync(save);
    }

    // Trusted DeckBuilder Continue helpers
    public async Task SetPendingDeckContinueAsync(string romKey, string? title)
    {
        try
        {
            var save = await LoadAsync();
            save.PendingDeckContinue = true;
            save.PendingDeckContinueRom = romKey;
            save.PendingDeckContinueTitle = string.IsNullOrWhiteSpace(title) ? romKey : title;
            save.PendingDeckContinueAtUtc = DateTime.UtcNow;
            await SaveAsync(save);
        }
        catch { }
    }

    public async Task ClearPendingDeckContinueAsync()
    {
        try
        {
            var save = await LoadAsync();
            if (save.PendingDeckContinue || !string.IsNullOrWhiteSpace(save.PendingDeckContinueRom) || !string.IsNullOrWhiteSpace(save.PendingDeckContinueTitle))
            {
                save.PendingDeckContinue = false;
                save.PendingDeckContinueRom = null;
                save.PendingDeckContinueTitle = null;
                save.PendingDeckContinueAtUtc = null;
                await SaveAsync(save);
            }
        }
        catch { }
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
