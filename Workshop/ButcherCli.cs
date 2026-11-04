using System;
using System.IO;
using System.Text.Json;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Butchering mode in the headless runners (--romtest / --session-test): <c>--butcher &lt;settings.json&gt; [--butcher-at N]</c>
/// applies a Butchering-mode settings file (the panel's own JSON, camelCase) at frame N (default 60), so UAT/cases/butcher.json
/// can hold golden hashes for a fixed set of effects per console, and an all-off file must reproduce the plain run exactly.
/// </summary>
public static class ButcherCli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static (ButcherSettings? settings, int at) Parse(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--butcher", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return (null, -1);
        var s = JsonSerializer.Deserialize<ButcherSettings>(File.ReadAllText(args[i + 1]), Json) ?? throw new InvalidDataException("empty Butchering settings");
        int j = Array.FindIndex(args, a => a.Equals("--butcher-at", StringComparison.OrdinalIgnoreCase));
        int at = j >= 0 && j + 1 < args.Length ? int.Parse(args[j + 1]) : 60;
        return (s, at);
    }

    /// <summary>The host a session's Butchering mode drives (the Sega session under its picture bridge).</summary>
    public static IButcherHost? Host(object session) =>
        (session is NesEmulator.Mix.SegaPictureSession sp ? sp.Inner : session) as IButcherHost;
}
