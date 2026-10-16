using System;
using System.IO;
using System.Text.Json;

namespace BrokenNes.Dev;

/// <summary>
/// BrokenDev is a developer-only module: the app shows nothing of it unless it is switched on. The switch is either the
/// environment variable <c>BROKENNES_DEV=1</c> or <c>"brokenDevEnabled": true</c> in the app's <c>config.json</c>
/// (<c>%APPDATA%\BrokenNes\config.json</c>, the same file <c>EmulatorConfig</c> writes). This class has no UI dependency, so the
/// desktop app and the Workshop command line apply exactly the same rule.
/// </summary>
public static class BrokenDevGate
{
    public const string EnvVar = "BROKENNES_DEV";

    /// <summary>The config file the desktop app uses. <c>BROKENNES_CONFIG_DIR</c> overrides the folder (tests point it at a scratch folder).</summary>
    public static string ConfigPath()
    {
        string? dir = Environment.GetEnvironmentVariable("BROKENNES_CONFIG_DIR");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes");
        return Path.Combine(dir, "config.json");
    }

    /// <summary>True when BrokenDev is switched on; <paramref name="why"/> says which switch did it, or what is missing.</summary>
    public static bool IsEnabled(out string why)
    {
        if (Environment.GetEnvironmentVariable(EnvVar) == "1") { why = EnvVar + "=1"; return true; }
        try
        {
            string path = ConfigPath();
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("brokenDevEnabled", out var p) && p.ValueKind == JsonValueKind.True)
                {
                    why = "brokenDevEnabled in " + path;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            why = "config.json unreadable (" + ex.Message + ")";
            return false;
        }
        why = $"neither {EnvVar}=1 nor \"brokenDevEnabled\": true in {ConfigPath()}";
        return false;
    }

    public static bool IsEnabled() => IsEnabled(out _);
}
