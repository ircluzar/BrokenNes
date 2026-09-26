using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>
/// Finds user-supplied coprocessor firmware (never bundled - these are Nintendo/NEC program ROMs)
/// and builds the cartridge chip for a game. Searched, in order: $BROKENNES_SNES_FIRMWARE, the
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

    /// <summary>
    /// The chip this cartridge needs, or null when it needs none. <paramref name="note"/> says what
    /// was loaded or what is missing, for the window title / probe report.
    /// </summary>
    public static ISnesCoprocessor? CreateCoprocessor(SnesCartridge cart, string romPath, out string note)
    {
        note = "";
        switch (cart.Chip)
        {
            case SnesChip.None: return null;
            case SnesChip.Dsp:
            {
                // DSP-1B fixed bugs in DSP-1, but Pilotwings was mastered against the original.
                bool pilotwings = cart.Title.StartsWith("PILOTWINGS", StringComparison.OrdinalIgnoreCase);
                string[] names = pilotwings ? new[] { "dsp1.rom", "dsp1b.rom" } : new[] { "dsp1b.rom", "dsp1.rom" };
                string? path = Find(romPath, names);
                if (path == null) { note = "DSP-1 firmware missing (dsp1b.rom)"; return null; }
                byte[] image = File.ReadAllBytes(path);
                if (image.Length != 8192) { note = $"{Path.GetFileName(path)} is {image.Length} bytes, expected 8192"; return null; }
                string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant().Replace("DSP", "DSP-");
                note = $"{name} from {path}";
                return new NECDSP_SFC(image, NECDSP_SFC.MappingFor(cart), name);
            }
            default:
                note = $"coprocessor {cart.Chip} (chipset ${cart.ChipsetByte:X2}) not emulated yet";
                return null;
        }
    }
}
