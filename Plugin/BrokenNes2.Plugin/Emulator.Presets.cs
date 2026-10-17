// Round 6 (presets): the emulator's side of the Presets chips. A Presets chip (Cx.PresetChips) is a sound chip that is not a core: no sound unit is built, every instance on the emulator becomes a player of its own preset (Bn2Plugin.Presets.cs), in Direct mode only.
namespace BrokenNes2;

public sealed partial class Emulator
{
    /// <summary>Direct mode with a Presets chip: every instance plays the preset it holds, on an engine of its own, with no ROM.</summary>
    public bool PresetMode => Mode == 0 && Cx.IsPresetChip(Chip);

    /// <summary>The console short name (NES, GB, SNES, SMS, MD) the preset picker lists, or null for every console's presets.</summary>
    public string? PresetFilter => PresetMode ? Cx.PresetFilterOf(Chip) : null;

    /// <summary>The SHA-1 (lower-case hex) of the game this emulator runs, or "" when none is loaded: a preset keeps it for reference (never the ROM).</summary>
    internal string SourceGameSha1()
    {
        string? id;
        lock (romLock) id = game?.GameId;
        return id is { Length: >= 40 } && id[^40..].All(Uri.IsHexDigit) ? id[^40..].ToLowerInvariant() : "";
    }

    /// <summary>A game is going to run on this emulator: it needs its console's own sound chip, so a Presets chip goes back to the console's first one. (EmulatorHub.Gate held.)</summary>
    private void LeavePresetChipLocked()
    {
        if (Cx.IsPresetChip(chip)) Volatile.Write(ref chip, Cx.DefaultChip(Cx.ConsoleOf(chip)));
    }

    /// <summary>Puts this emulator on the Presets chip (Direct mode) so its instances play presets; the chip of the console they are on already when it is a Presets chip is kept. Refused, with the reason, while a game runs on it.</summary>
    internal string? EnterPresetMode(string? filter = null)
    {
        lock (EmulatorHub.Gate)
        {
            if (Mode == 1 && RomLoaded) return $"emulator #{Id} runs a game: pick another emulator (or switch it to Direct mode) to play presets";
            if (PresetMode) return null;
            SetChipLocked(Cx.IndexOfChip(Cx.PresetChips.First(p => p.Filter == filter).Id));
            Changed();
            return null;
        }
    }
}
