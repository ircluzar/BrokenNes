using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>A sound chip as the plugin lists it.</summary>
/// <param name="Id">What the Sound chip parameter shows (FIX, QN, DMG ...). In Direct mode this is also the NES-board APU the bare chip
/// is driven through: the Game Boy and SNES chips take the NES registers too (CoreRegistry ids DMG, DMGS, SNES).</param>
/// <param name="Console">0 NES, 1 Game Boy, 2 SNES, 3 Master System, 4 Genesis (see <see cref="Cx"/>).</param>
/// <param name="Note">A few words shown beside it in the list.</param>
/// <param name="IsPreset">One of the Presets chips (<see cref="Cx.PresetChips"/>): not a core, the plugin's own preset engines play. A flag so that asking it on the audio thread allocates nothing.</param>
public sealed record Chip(string Id, int Console, string? Note, bool IsPreset = false);

/// <summary>
/// The consoles BrokenNes2 offers and their sound chips. A console decides which chips are listed, and which CPU and PPU a running
/// game gets: always the best of the console's own family (BrokenNes's <see cref="CoreCatalog"/>: FIX for the NES, the real Game Boy
/// and SNES cores otherwise); those are not user choices any more.
/// </summary>
public static class Cx
{
    // Append-only (projects store these numbers). The Master System console (value 3) holds the SN76489 PSG chip (APU id SMS) and runs Master System and Game Gear games (the Game Gear's PSG is the same
    // chip); the Genesis console (value 4) holds the YM2612 + PSG chip (APU id MD) in Direct mode only: its games are NOT supported yet, see RomSupported.
    public const int Nes = 0, GameBoy = 1, Snes = 2, MasterSystem = 3, Genesis = 4, Count = 5;
    public static readonly string[] Names = ["NES", "Game Boy", "SNES", "SMS", "MD"];
    /// <summary>What the console buttons of the editor say (five of them side by side do not fit the long names).</summary>
    public static readonly string[] ShortNames = ["NES", "GB", "SNES", "SMS", "MD"];
    public static readonly string[] RomNames = ["NES", "Game Boy", "SNES", "Master System", "Genesis"];

    /// <summary>
    /// Whether ROM mode can run this console's games: all five. The Genesis joined with round 3 (its board and picture are certified: <c>Consoles.GenesisReady</c>; the game plays through Mix, and its sound unit's voices are stems, see
    /// <see cref="Channels"/>); the Master System and Game Gear before it. Every game runs through the console sessions like a Game Boy or SNES game.
    /// </summary>
    public static bool RomSupported(int console) => console is >= 0 and < Count;

    /// <summary>Cores that are not offered: MNES and WF turn the sound into MIDI-style playback rather than emulating a chip.</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "MNES", "WF" };

    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FIX"] = "accurate", ["FIXS"] = "8 channels", ["DMG"] = "Game Boy sound chip", ["DMGS"] = "8 channels", ["SNES"] = "S-DSP", ["SMS"] = "SN76489 PSG: 109 Hz and up",
        ["MD"] = "YM2612 FM + PSG: pick the patch with Instrument",
        ["SMSFM"] = "YM2413 FM + PSG: pick the instrument with Instrument",
    };

    /// <summary>Chips added after the first Sega release, in the order they were added; each is listed after every earlier chip whatever console it belongs to (a saved project stores the chip's index: nothing before it may move). Declared before <see cref="Chips"/>: static initializers run in order.</summary>
    private static readonly string[] Appended = ["SMSFM"];

    /// <summary>The Presets chips (Round 6), appended after every chip that existed (a saved project stores the chip's index). They are not cores: no sound unit is built for them, the plugin's own preset engines play (Presets/ and Bn2Plugin.Presets.cs), in Direct mode only, with no ROM.
    /// <c>PRESETS</c> offers the presets of every console (it sits in the NES list); each console's own list has a shortcut that opens the same picker filtered to that console's presets.</summary>
    public static readonly (string Id, int Console, string Note, string? Filter)[] PresetChips =
    [
        ("PRESETS", Nes, "presets of any console, no ROM", null),
        ("PRE-NES", Nes, "NES presets, no ROM", "NES"),
        ("PRE-GB", GameBoy, "Game Boy presets, no ROM", "GB"),
        ("PRE-SNES", Snes, "SNES presets, no ROM", "SNES"),
        ("PRE-SMS", MasterSystem, "Master System / Game Gear presets, no ROM", "SMS"),
        ("PRE-MD", Genesis, "Genesis presets, no ROM", "MD"),
    ];

    public static bool IsPresetId(string id) => PresetChips.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a chip (an index into <see cref="Chips"/>) is one of the Presets chips.</summary>
    public static bool IsPresetChip(int chip) => (uint)chip < (uint)Chips.Count && Chips[chip].IsPreset;       // (read on the audio thread every block: no allocation)

    /// <summary>The console short name (NES, GB, SNES, SMS, MD) a Presets chip's picker lists, or null for the chip that lists every console's presets (and for a chip that is not a Presets chip).</summary>
    public static string? PresetFilterOf(int chip) => (uint)chip < (uint)Chips.Count ? PresetChips.FirstOrDefault(p => p.Id.Equals(Chips[chip].Id, StringComparison.OrdinalIgnoreCase)).Filter : null;

    /// <summary>Every sound chip, grouped by console: NES chips (FIX first), then Game Boy, then SNES, then Master System (SMS), then Genesis (MD), then the chips appended since, in the order they were added (SMSFM, the Master System's FM chip, belongs to the
    /// Master System console but sits last). The Sound chip parameter is an index into this list: new chips are only ever appended (a core id must be given a family in <c>CoreCatalog</c> first, or it would join the NES chips and shift every index after it).</summary>
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
        foreach (var id in all.Where(i => NesFamily(i) == "Master System" && !Appended.Contains(i, StringComparer.OrdinalIgnoreCase)).OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            list.Add(new Chip(id, MasterSystem, Notes.GetValueOrDefault(id)));
        foreach (var id in all.Where(i => NesFamily(i) == "Genesis" && !Appended.Contains(i, StringComparer.OrdinalIgnoreCase)).OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            list.Add(new Chip(id, Genesis, Notes.GetValueOrDefault(id)));
        foreach (var id in Appended)
        {
            string? found = all.FirstOrDefault(i => i.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (found == null) continue;
            int console = NesFamily(found) switch { "Master System" => MasterSystem, "Genesis" => Genesis, "SNES" => Snes, "Game Boy" => GameBoy, _ => Nes };
            list.Add(new Chip(found, console, Notes.GetValueOrDefault(found)));
        }
        foreach (var p in PresetChips) list.Add(new Chip(p.Id, p.Console, p.Note, IsPreset: true));       // the Presets chips come last of all (append-only)
        return list;
    }

    // ---- the instrument pickers: the Instrument parameter is a patch of the chip's own bank for the FM chips ----

    /// <summary>What the Instrument parameter picks for this chip: 0 nothing (the NES chips: Pulse duty is the control), 1 the Genesis chip's FM patch, 2 the Master System FM chip's instrument.</summary>
    public static int PatchBankOf(string chipId) => chipId.ToUpperInvariant() switch { "MD" => 1, "SMSFM" => 2, _ => 0 };

    public static int PatchCount(int bank) => bank switch { 1 => NesEmulator.Mix.FmPatchBank.Count, 2 => NesEmulator.Mix.OpllPatchBank.Count, _ => 0 };

    /// <summary>A patch index made safe (out of range is the bank's last entry, the PSG square, as the bridges clamp it).</summary>
    public static int PatchClamp(int bank, int index) => bank switch { 1 => NesEmulator.Mix.FmPatchBank.Clamp(index), 2 => NesEmulator.Mix.OpllPatchBank.Clamp(index), _ => index };

    /// <summary>The picker's rows: a name, a note beside it, and a line that says what the patch is.</summary>
    public static (string Name, string Note, string Description)[] PatchItems(int bank) => bank switch
    {
        1 => NesEmulator.Mix.FmPatchBank.Patches.Select(p => (p.Name, p.Psg ? "PSG tones, no FM" : $"algorithm {p.Algorithm}", p.Description)).ToArray(),
        2 => NesEmulator.Mix.OpllPatchBank.Patches.Select(p => (p.Name, p.Psg ? "PSG tones, no FM" : p.Rom > 0 ? $"chip instrument {p.Rom}" : "designed", p.Description)).ToArray(),
        _ => [],
    };

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

    public static ConsoleKind Kind(int console) => console switch { GameBoy => ConsoleKind.GameBoy, Snes => ConsoleKind.Snes, MasterSystem => ConsoleKind.MasterSystem, Genesis => ConsoleKind.Genesis, _ => ConsoleKind.Nes };

    /// <summary>The plugin console a game of this kind runs on. The Master System and the Game Gear (and the SG-1000) share the Master System console. A kind the plugin does not run throws, never quietly becoming a NES game.</summary>
    public static int FromKind(ConsoleKind k) => k switch
    {
        ConsoleKind.Snes => Snes,
        ConsoleKind.GameBoy or ConsoleKind.GameBoyColor => GameBoy,
        ConsoleKind.Nes => Nes,
        ConsoleKind.MasterSystem or ConsoleKind.GameGear => MasterSystem,
        ConsoleKind.Genesis => Genesis,
        _ => throw new NotSupportedException($"the plugin does not run {NesEmulator.Systems.Consoles.DisplayName(k)} games yet"),
    };

    /// <summary>
    /// The channels (other than Mix) an instance can sit on for a console, mode and sound chip, in the order the editor lists them: the four tone channels of the NES model in Direct mode and for a NES game; a Sega game's stems in ROM mode (the PSG's
    /// four voices, then the FM chip's: the Genesis' FM 1 to 6 and DAC, the Master System FM chip's FM 1 to 9 and five rhythm voices); none for a Game Boy or SNES game (those play through Mix).
    /// </summary>
    public static int[] Channels(int console, int mode, string chipId)
    {
        if (mode == 0 || console == Nes) return [Ch.Pulse1, Ch.Pulse2, Ch.Triangle, Ch.Noise];
        if (console == Genesis) return [Ch.Pulse1, Ch.Pulse2, Ch.Triangle, Ch.Noise, .. Enumerable.Range(Ch.Fm1, 6), Ch.Dac];
        if (console == MasterSystem)
            return chipId.Equals("SMSFM", StringComparison.OrdinalIgnoreCase)
                ? [Ch.Pulse1, Ch.Pulse2, Ch.Triangle, Ch.Noise, .. Enumerable.Range(Ch.Fm1, 9), Ch.Bass, Ch.HiHat, Ch.Snare, Ch.Tom, Ch.Cymbal]
                : [Ch.Pulse1, Ch.Pulse2, Ch.Triangle, Ch.Noise];
        return [];
    }

    /// <summary>The CPU and PPU a game of this console runs on: the best of its own family.</summary>
    public static string BestCpu(int console) => CoreCatalog.Default(Kind(console), CoreSlot.Cpu);
    public static string BestPpu(int console) => CoreCatalog.Default(Kind(console), CoreSlot.Ppu);

    /// <summary>The sound chip of a running game: the NES chips as they are; the Game Boy's own sound unit (GB, GBS for the 8-channel
    /// one) and the SNES's S-SMP + S-DSP replace the register bridges that Direct mode uses.</summary>
    public static string RomApu(Chip chip) => IsPresetId(chip.Id) ? throw new InvalidOperationException("the Presets chips play in Direct mode only: a game cannot run on them (the emulator should have gone back to the console's own sound chip)") : chip.Console switch
    {
        Nes => chip.Id,
        GameBoy => chip.Id.Equals("DMGS", StringComparison.OrdinalIgnoreCase) ? "GBS" : "GB",
        MasterSystem => chip.Id.Equals("SMSFM", StringComparison.OrdinalIgnoreCase) ? "SMS-FM" : "SMS",   // the stock unit (the PSG; a Game Gear game takes its own menu id in SessionGame), or the Japanese console's PSG + YM2413 for the FM chip
        Genesis => "MD",         // the stock Genesis unit (GenesisSoundFactories id): the Z80, the YM2612 with its DAC and the PSG
        _ => "SFC",
    };

    /// <summary>The file types the Load dialog and the Game list take.</summary>
    public const string RomFilter = "Game ROMs (*.nes;*.gb;*.gbc;*.sfc;*.smc;*.sms;*.gg;*.sg;*.sc;*.md;*.gen;*.smd;*.zip)|*.nes;*.gb;*.gbc;*.sfc;*.smc;*.sms;*.gg;*.sg;*.sc;*.md;*.gen;*.smd;*.zip|All files (*.*)|*.*";
}
