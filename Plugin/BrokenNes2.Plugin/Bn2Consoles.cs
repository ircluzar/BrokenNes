using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>A sound chip as the plugin lists it.</summary>
/// <param name="Id">What the Sound chip parameter shows (FIX, QN, DMG ...). In Direct mode this is also the NES-board APU the bare chip
/// is driven through: the Game Boy and SNES chips take the NES registers too (CoreRegistry ids DMG, DMGS, SNES).</param>
/// <param name="Console">0 NES, 1 Game Boy, 2 SNES (see <see cref="Cx"/>).</param>
/// <param name="Note">A few words shown beside it in the list.</param>
public sealed record Chip(string Id, int Console, string? Note);

/// <summary>
/// The consoles BrokenNes2 offers and their sound chips. A console decides which chips are listed, and which CPU and PPU a running
/// game gets: always the best of the console's own family (BrokenNes's <see cref="CoreCatalog"/>: FIX for the NES, the real Game Boy
/// and SNES cores otherwise); those are not user choices any more.
/// </summary>
public static class Cx
{
    public const int Nes = 0, GameBoy = 1, Snes = 2, Count = 3;
    public static readonly string[] Names = ["NES", "Game Boy", "SNES"];
    public static readonly string[] RomNames = ["NES", "Game Boy", "SNES"];

    /// <summary>Cores that are not offered: MNES and WF turn the sound into MIDI-style playback rather than emulating a chip.</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "MNES", "WF" };

    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FIX"] = "accurate", ["FIXS"] = "8 channels", ["DMG"] = "Game Boy sound chip", ["DMGS"] = "8 channels", ["SNES"] = "S-DSP",
    };

    /// <summary>Every sound chip, grouped by console: NES chips (FIX first), then Game Boy, then SNES. The Sound chip parameter is an index into this list.</summary>
    public static IReadOnlyList<Chip> Chips { get; } = Build();

    private static string NesFamily(string id) => CoreCatalog.NesFamilyOf(CoreSlot.Apu, id);

    private static List<Chip> Build()
    {
        var all = CoreRegistry.ApuIds.Where(id => !Hidden.Contains(id)).ToList();
        var list = new List<Chip>();
        foreach (var id in all.Where(i => NesFamily(i) == "NES")
                     .OrderBy(i => i.Equals("FIX", StringComparison.OrdinalIgnoreCase) ? 0 : i.Equals("FIXS", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                     .ThenBy(i => i, StringComparer.OrdinalIgnoreCase))
            list.Add(new Chip(id, Nes, Notes.GetValueOrDefault(id)));
        foreach (var id in all.Where(i => NesFamily(i) == "Game Boy").OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            list.Add(new Chip(id, GameBoy, Notes.GetValueOrDefault(id)));
        foreach (var id in all.Where(i => NesFamily(i) == "SNES").OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            list.Add(new Chip(id, Snes, Notes.GetValueOrDefault(id)));
        return list;
    }

    public static int ConsoleOf(int chip) => Chips[Math.Clamp(chip, 0, Chips.Count - 1)].Console;

    /// <summary>The chips of a console, as indices into <see cref="Chips"/>.</summary>
    public static int[] ChipsOf(int console) => Enumerable.Range(0, Chips.Count).Where(i => Chips[i].Console == console).ToArray();

    /// <summary>The chip a console starts with (the first of its own: FIX, DMG, SNES).</summary>
    public static int DefaultChip(int console) => ChipsOf(console).FirstOrDefault();

    public static int IndexOfChip(string id)
    {
        for (int i = 0; i < Chips.Count; i++) if (Chips[i].Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    public static ConsoleKind Kind(int console) => console switch { GameBoy => ConsoleKind.GameBoy, Snes => ConsoleKind.Snes, _ => ConsoleKind.Nes };

    public static int FromKind(ConsoleKind k) => k switch { ConsoleKind.Snes => Snes, ConsoleKind.GameBoy or ConsoleKind.GameBoyColor => GameBoy, _ => Nes };

    /// <summary>The CPU and PPU a game of this console runs on: the best of its own family.</summary>
    public static string BestCpu(int console) => CoreCatalog.Default(Kind(console), CoreSlot.Cpu);
    public static string BestPpu(int console) => CoreCatalog.Default(Kind(console), CoreSlot.Ppu);

    /// <summary>The sound chip of a running game: the NES chips as they are; the Game Boy's own sound unit (GB, GBS for the 8-channel
    /// one) and the SNES's S-SMP + S-DSP replace the register bridges that Direct mode uses.</summary>
    public static string RomApu(Chip chip) => chip.Console switch
    {
        Nes => chip.Id,
        GameBoy => chip.Id.Equals("DMGS", StringComparison.OrdinalIgnoreCase) ? "GBS" : "GB",
        _ => "SFC",
    };

    /// <summary>The file types the Load dialog and the Game list take.</summary>
    public const string RomFilter = "Game ROMs (*.nes;*.gb;*.gbc;*.sfc;*.smc;*.zip)|*.nes;*.gb;*.gbc;*.sfc;*.smc;*.zip|All files (*.*)|*.*";
}
