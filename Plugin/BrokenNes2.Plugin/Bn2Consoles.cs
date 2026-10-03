using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>A sound chip as the plugin lists it.</summary>
/// <param name="Id">What the Sound chip parameter shows (FIX, QN, DMG ...). In Direct mode this is also the NES-board APU the bare chip
/// is driven through: the Game Boy and SNES chips take the NES registers too (CoreRegistry ids DMG, DMGS, SNES).</param>
/// <param name="Console">0 NES, 1 Game Boy, 2 SNES, 3 Master System (see <see cref="Cx"/>).</param>
/// <param name="Note">A few words shown beside it in the list.</param>
public sealed record Chip(string Id, int Console, string? Note);

/// <summary>
/// The consoles BrokenNes2 offers and their sound chips. A console decides which chips are listed, and which CPU and PPU a running
/// game gets: always the best of the console's own family (BrokenNes's <see cref="CoreCatalog"/>: FIX for the NES, the real Game Boy
/// and SNES cores otherwise); those are not user choices any more.
/// </summary>
public static class Cx
{
    // Append-only (projects store these numbers). The Master System console (value 3) holds the SN76489 PSG chip (APU id SMS); the Game Gear's PSG is the same chip and the Genesis
    // joins later as value 4 with its own chips. Its games are NOT supported yet: see RomSupported.
    public const int Nes = 0, GameBoy = 1, Snes = 2, MasterSystem = 3, Count = 4;
    public static readonly string[] Names = ["NES", "Game Boy", "SNES", "SMS"];
    public static readonly string[] RomNames = ["NES", "Game Boy", "SNES", "Master System"];

    /// <summary>
    /// Whether ROM mode can run this console's games. The Master System console exists for its sound chip in Direct mode only: its board is not finished (the Sega consoles stay behind
    /// <c>Consoles.MasterSystemReady</c>), and the plugin has no PSG stems or Runaway sampler yet. What ROM mode will need once the board exists is written down in Plugin/README.md
    /// ("The Master System console"); until then a Master System game is refused with a message, never run as something else.
    /// </summary>
    public static bool RomSupported(int console) => console != MasterSystem;

    /// <summary>Cores that are not offered: MNES and WF turn the sound into MIDI-style playback rather than emulating a chip.</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "MNES", "WF" };

    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FIX"] = "accurate", ["FIXS"] = "8 channels", ["DMG"] = "Game Boy sound chip", ["DMGS"] = "8 channels", ["SNES"] = "S-DSP", ["SMS"] = "SN76489 PSG: 109 Hz and up",
    };

    /// <summary>Every sound chip, grouped by console: NES chips (FIX first), then Game Boy, then SNES, then Master System. The Sound chip parameter is an index into this list: new chips are
    /// only ever appended (a core id must be given a family in <c>CoreCatalog</c> first, or it would join the NES chips and shift every index after it).</summary>
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
        foreach (var id in all.Where(i => NesFamily(i) == "Master System").OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            list.Add(new Chip(id, MasterSystem, Notes.GetValueOrDefault(id)));
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

    public static ConsoleKind Kind(int console) => console switch { GameBoy => ConsoleKind.GameBoy, Snes => ConsoleKind.Snes, MasterSystem => ConsoleKind.MasterSystem, _ => ConsoleKind.Nes };

    /// <summary>The plugin console a game of this kind runs on. The Master System and Game Gear map to the Master System console only once their board is finished and ROM mode supports it
    /// (<see cref="RomSupported"/>); until then they throw, never quietly becoming a NES game.</summary>
    public static int FromKind(ConsoleKind k) => k switch
    {
        ConsoleKind.Snes => Snes,
        ConsoleKind.GameBoy or ConsoleKind.GameBoyColor => GameBoy,
        ConsoleKind.Nes => Nes,
        _ => throw new NotSupportedException($"the plugin does not run {NesEmulator.Systems.Consoles.DisplayName(k)} games yet"),
    };

    /// <summary>The CPU and PPU a game of this console runs on: the best of its own family.</summary>
    public static string BestCpu(int console) => CoreCatalog.Default(Kind(console), CoreSlot.Cpu);
    public static string BestPpu(int console) => CoreCatalog.Default(Kind(console), CoreSlot.Ppu);

    /// <summary>The sound chip of a running game: the NES chips as they are; the Game Boy's own sound unit (GB, GBS for the 8-channel
    /// one) and the SNES's S-SMP + S-DSP replace the register bridges that Direct mode uses.</summary>
    public static string RomApu(Chip chip) => chip.Console switch
    {
        Nes => chip.Id,
        GameBoy => chip.Id.Equals("DMGS", StringComparison.OrdinalIgnoreCase) ? "GBS" : "GB",
        MasterSystem => "SMS",   // the stock sound unit (SmsSoundFactories id) once Master System games run; unused until then
        _ => "SFC",
    };

    /// <summary>The file types the Load dialog and the Game list take.</summary>
    public const string RomFilter = "Game ROMs (*.nes;*.gb;*.gbc;*.sfc;*.smc;*.zip)|*.nes;*.gb;*.gbc;*.sfc;*.smc;*.zip|All files (*.*)|*.*";
}
