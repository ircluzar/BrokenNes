using System;

namespace NesEmulator.Snes;

/// <summary>
/// Builds the cartridge chip a game needs. Front ends pass a firmware loader (file system on
/// desktop, none in the browser); when real DSP firmware is available it is used (exact), and
/// otherwise the homemade DSP-1 (<see cref="DSP1_SFC"/>) is bundled in, so no external file is
/// ever required.
/// </summary>
public static class SnesChips
{
    /// <param name="loadFirmware">Returns the bytes of the first of these file names it can find, or null.</param>
    /// <param name="preferHomemade">Use the homemade DSP-1 even when real firmware is present.</param>
    public static ISnesCoprocessor? Create(SnesCartridge cart, Func<string[], (string name, byte[] data)?>? loadFirmware,
                                           bool preferHomemade, out string note)
    {
        note = "";
        switch (cart.Chip)
        {
            case SnesChip.None: return null;
            case SnesChip.Dsp:
            {
                var mapping = NECDSP_SFC.MappingFor(cart);
                // DSP-1B fixed bugs in DSP-1, but Pilotwings was mastered against the original.
                bool pilotwings = cart.Title.StartsWith("PILOTWINGS", StringComparison.OrdinalIgnoreCase);
                string[] names = pilotwings ? new[] { "dsp1.rom", "dsp1b.rom" } : new[] { "dsp1b.rom", "dsp1.rom" };
                if (!preferHomemade && loadFirmware?.Invoke(names) is { } fw && fw.data.Length == 8192)
                {
                    string label = System.IO.Path.GetFileNameWithoutExtension(fw.name).ToUpperInvariant().Replace("DSP", "DSP-");
                    note = $"{label} firmware ({fw.name})";
                    return new NECDSP_SFC(fw.data, mapping, label);
                }
                note = "homemade DSP-1 (no firmware needed)";
                return new DSP1_SFC(mapping);
            }
            case SnesChip.Sa1:
                note = "SA-1";
                return new SA1_SFC(cart);
            case SnesChip.SuperFx:
            {
                var gsu = new GSU_SFC(cart);
                note = gsu.Name;
                return gsu;
            }
            default:
                note = $"coprocessor {cart.Chip} (chipset ${cart.ChipsetByte:X2}) not emulated yet";
                return null;
        }
    }
}
