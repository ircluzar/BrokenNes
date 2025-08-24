using System.Text.Json;
using Microsoft.JSInterop;
using BrokenNes.Models;
using NesEmulator;

namespace BrokenNes.Services;

public class GameSaveService
{
    private const string StorageKey = "game_save_v1";
    private readonly IJSRuntime _js;

    public GameSaveService(IJSRuntime js)
    {
        _js = js;
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

    // Reflectively count available cores (CPU + PPU + APU) using CoreRegistry.
    public int GetUnlockedCoresCount()
    {
        try
        {
            // Assume all discovered cores are unlocked for now.
            var cpu = CoreRegistry.CpuIds?.Count ?? 0;
            var ppu = CoreRegistry.PpuIds?.Count ?? 0;
            var apu = CoreRegistry.ApuIds?.Count ?? 0;
            return cpu + ppu + apu;
        }
        catch { return 0; }
    }
}
