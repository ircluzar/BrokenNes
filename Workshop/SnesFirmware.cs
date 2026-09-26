using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>
/// Finds optional user-supplied coprocessor firmware (never bundled - these are Nintendo/NEC program
/// ROMs) and builds the cartridge chip for a game; without firmware the homemade DSP-1 is used. Searched, in order: $BROKENNES_SNES_FIRMWARE, the
/// ROM's folder, a "firmware" folder beside the ROM, %APPDATA%\BrokenNes\Firmware, and a
/// "firmware" folder beside the executable.
/// </summary>
internal static class SnesFirmware
{
    public static IEnumerable<string> SearchDirs(string romPath)
    {
        string? env = Environment.GetEnvironmentVariable("BROKENNES_SNES_FIRMWARE");
        if (!string.IsNullOrEmpty(env)) yield return env;
        string? romDir = Path.GetDirectoryName(Path.GetFullPath(romPath));
        if (romDir != null) { yield return romDir; yield return Path.Combine(romDir, "firmware"); }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes", "Firmware");
        yield return Path.Combine(AppContext.BaseDirectory, "firmware");
    }

    public static string? Find(string romPath, params string[] names)
    {
        foreach (var dir in SearchDirs(romPath))
            foreach (var name in names)
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>Set by --dsp1-homemade: use the bundled DSP-1 even when real firmware is found.</summary>
    public static bool PreferHomemade;

    /// <summary>
    /// The chip this cartridge needs, or null when it needs none. <paramref name="note"/> says what
    /// was loaded or what is missing, for the window title / probe report.
    /// </summary>
    public static ISnesCoprocessor? CreateCoprocessor(SnesCartridge cart, string romPath, out string note) =>
        SnesChips.Create(cart, names => Find(romPath, names) is { } p ? (p, File.ReadAllBytes(p)) : null, PreferHomemade, out note);
}
