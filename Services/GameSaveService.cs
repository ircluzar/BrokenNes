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
                    return loaded;
                }
            }
        }
        catch { }
        return new GameSave();
    }

    public async Task SaveAsync(GameSave save)
    {
        if (save.Level < 1) save.Level = 1;
        save.Achievements ??= new();
        try
        {
            var json = JsonSerializer.Serialize(save);
            await _js.InvokeVoidAsync("nesInterop.idbSetItem", StorageKey, json);
        }
        catch { }
    }

    // Reflectively count available cores across all categories (CPU + PPU + APU + CLOCK + SHADER).
    public int GetUnlockedCoresCount()
    {
        try
        {
        // Assume all discovered items are unlocked for now.
        var cpu = CoreRegistry.CpuIds?.Count ?? 0;
        var ppu = CoreRegistry.PpuIds?.Count ?? 0;
        var apu = CoreRegistry.ApuIds?.Count ?? 0;
        var clocks = 0; try { clocks = ClockRegistry.Ids?.Count ?? 0; } catch { clocks = 0; }
        var shaders = 0; try { shaders = _shaderProvider?.All?.Count ?? 0; } catch { shaders = 0; }
        return cpu + ppu + apu + clocks + shaders;
        }
        catch { return 0; }
    }

    // Total number of discoverable cores across all categories.
    // For now this is identical to GetUnlockedCoresCount until we add locked core logic.
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
