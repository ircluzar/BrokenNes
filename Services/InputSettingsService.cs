using System.Text.Json;
using Microsoft.JSInterop;
using BrokenNes.Models;

namespace BrokenNes.Services;

public class InputSettingsService
{
    private const string StorageKey = "input_settings_v1";
    private readonly IJSRuntime _js;

    public InputSettingsService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<InputSettings> LoadAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("nesInterop.idbGetItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var loaded = JsonSerializer.Deserialize<InputSettings>(json, opts);
                if (loaded != null) return loaded;
            }
        }
        catch { }
        // default
        return new InputSettings();
    }

    public async Task SaveAsync(InputSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        try { await _js.InvokeVoidAsync("nesInterop.idbSetItem", StorageKey, json); }
        catch { }
    }

    // JS helpers for controllers
    public async Task<GamepadInfo[]> GetConnectedGamepadsAsync()
    {
        var arr = await _js.InvokeAsync<GamepadInfo[]>("nesInput.getGamepads");
        return arr ?? Array.Empty<GamepadInfo>();
    }
}

public class GamepadInfo
{
    public int Index { get; set; }
    public string Id { get; set; } = string.Empty;
    public bool Connected { get; set; }
    public int Buttons { get; set; }
    public int Axes { get; set; }
}
