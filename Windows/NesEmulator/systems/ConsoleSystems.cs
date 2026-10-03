using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace NesEmulator.Systems;

/// <summary>
/// The consoles BrokenNes 2 runs. Game Boy and Game Boy Color share cartridges; the model differs. The Sega consoles (Master System, Game Gear,
/// Genesis / Mega Drive) are appended: stored settings use <see cref="Consoles.Key"/>, never these numbers. They stay out of every menu, ROM
/// detection and file filter until their cores exist (see <see cref="Consoles.IsAvailable"/>).
/// </summary>
public enum ConsoleKind { Nes, Snes, GameBoy, GameBoyColor, MasterSystem, GameGear, Genesis }

/// <summary>One controller, the union of every console's buttons. Each console reads the ones it has.</summary>
/// <remarks>
/// Sega: Master System / Game Gear buttons 1 and 2 are A and B; the SMS Pause button is Select; the Game Gear Start is Start. The Genesis pad is A, B, C,
/// X, Y, Z, Mode and Start (C, Z and Mode are the three bits added for it). One bit (15) is still free.
/// </remarks>
[Flags]
public enum PadButtons : ushort
{
    None = 0,
    Up = 1 << 0, Down = 1 << 1, Left = 1 << 2, Right = 1 << 3,
    A = 1 << 4, B = 1 << 5, X = 1 << 6, Y = 1 << 7, L = 1 << 8, R = 1 << 9,
    Start = 1 << 10, Select = 1 << 11,
    C = 1 << 12, Z = 1 << 13, Mode = 1 << 14,
}

/// <summary>The three swappable parts of a console.</summary>
public enum CoreSlot { Cpu, Ppu, Apu }

public static class Consoles
{
    // The Sega consoles are built behind a switch. Until a console's core is finished it is invisible: not in menus (All), not recognised by ROM
    // detection, not in a file filter. A build with unfinished Sega work therefore behaves exactly like one without it. The foundation preview
    // (BROKENNES_SEGA=1 on the desktop and Workshop, or SegaPreview = true from Lite's page) shows all three, each running a placeholder session so the
    // plumbing can be exercised end to end. A track sets its console's Ready flag when its core is certified (and replaces the placeholder in
    // ConsoleSessions.Create at the same time: Create refuses a Ready console that still has none).
    public static readonly bool MasterSystemReady = false, GameGearReady = false, GenesisReady = false;

    public static bool SegaPreview { get; set; } = Environment.GetEnvironmentVariable("BROKENNES_SEGA") == "1";

    public static bool IsAvailable(ConsoleKind k) => k switch
    {
        ConsoleKind.MasterSystem => MasterSystemReady || SegaPreview,
        ConsoleKind.GameGear => GameGearReady || SegaPreview,
        ConsoleKind.Genesis => GenesisReady || SegaPreview,
        _ => true,
    };

    /// <summary>True when a finished core (not just the preview) backs the console.</summary>
    public static bool IsReady(ConsoleKind k) => k switch
    {
        ConsoleKind.MasterSystem => MasterSystemReady,
        ConsoleKind.GameGear => GameGearReady,
        ConsoleKind.Genesis => GenesisReady,
        _ => true,
    };

    private static readonly ConsoleKind[] Every =
        { ConsoleKind.Nes, ConsoleKind.Snes, ConsoleKind.GameBoy, ConsoleKind.GameBoyColor, ConsoleKind.MasterSystem, ConsoleKind.GameGear, ConsoleKind.Genesis };

    /// <summary>The consoles the apps offer: the finished ones (plus the Sega ones while the preview is on).</summary>
    public static ConsoleKind[] All => Every.Where(IsAvailable).ToArray();

    public static string DisplayName(ConsoleKind k) => k switch
    {
        ConsoleKind.Nes => "NES",
        ConsoleKind.Snes => "SNES",
        ConsoleKind.GameBoy => "Game Boy",
        ConsoleKind.MasterSystem => "Master System",
        ConsoleKind.GameGear => "Game Gear",
        ConsoleKind.Genesis => "Genesis / Mega Drive",
        _ => "Game Boy Color",
    };

    /// <summary>Short stable key for settings and storage ("nes", "snes", "gb", "gbc", "sms", "gg", "md").</summary>
    public static string Key(ConsoleKind k) => k switch
    {
        ConsoleKind.Nes => "nes", ConsoleKind.Snes => "snes", ConsoleKind.GameBoy => "gb",
        ConsoleKind.MasterSystem => "sms", ConsoleKind.GameGear => "gg", ConsoleKind.Genesis => "md",
        _ => "gbc",
    };

    public static ConsoleKind FromKey(string? key) => (key ?? "").ToLowerInvariant() switch
    {
        "snes" or "sfc" => ConsoleKind.Snes,
        "gb" or "gameboy" => ConsoleKind.GameBoy,
        "gbc" or "gameboycolor" => ConsoleKind.GameBoyColor,
        "sms" or "mastersystem" or "master system" => ConsoleKind.MasterSystem,
        "gg" or "gamegear" or "game gear" => ConsoleKind.GameGear,
        "md" or "genesis" or "megadrive" or "mega drive" or "gen" => ConsoleKind.Genesis,
        _ => ConsoleKind.Nes,
    };

    /// <summary>The core family that is native to a console ("NES", "SNES", "Game Boy", "Master System", "Genesis").</summary>
    public static string Family(ConsoleKind k) => k switch
    {
        ConsoleKind.Nes => "NES", ConsoleKind.Snes => "SNES",
        ConsoleKind.MasterSystem or ConsoleKind.GameGear => "Master System",
        ConsoleKind.Genesis => "Genesis",
        _ => "Game Boy",
    };

    public static bool IsGameBoy(ConsoleKind k) => k is ConsoleKind.GameBoy or ConsoleKind.GameBoyColor;
    /// <summary>Master System or Game Gear (one family of chips: Z80, the SMS VDP, the SN76489).</summary>
    public static bool IsMasterSystemFamily(ConsoleKind k) => k is ConsoleKind.MasterSystem or ConsoleKind.GameGear;
    public static bool IsSega(ConsoleKind k) => k is ConsoleKind.MasterSystem or ConsoleKind.GameGear or ConsoleKind.Genesis;

    /// <summary>File extensions each console's ROMs use (lower case, with the dot). Master System covers the SG-1000 (.sg), SC-3000 (.sc) and Mark III.</summary>
    public static string[] Extensions(ConsoleKind k) => k switch
    {
        ConsoleKind.Nes => new[] { ".nes" },
        ConsoleKind.Snes => new[] { ".sfc", ".smc" },
        ConsoleKind.MasterSystem => new[] { ".sms", ".sg", ".sc" },
        ConsoleKind.GameGear => new[] { ".gg" },
        ConsoleKind.Genesis => new[] { ".md", ".gen", ".smd", ".bin" },
        _ => new[] { ".gb", ".gbc" },
    };

    /// <summary>Every ROM extension BrokenNes 2 opens (the Sega ones only once available), plus .zip (a ROM inside is unpacked).</summary>
    public static string[] AllRomExtensions =>
        new[] { ".nes", ".sfc", ".smc", ".gb", ".gbc" }
            .Concat(new[] { ConsoleKind.MasterSystem, ConsoleKind.GameGear, ConsoleKind.Genesis }.Where(IsAvailable).SelectMany(Extensions))
            .Append(".zip").ToArray();
}

/// <summary>Works out which console a ROM belongs to, from its contents first and its file name second.</summary>
public static class RomDetect
{
    private static readonly byte[] NintendoLogoStart = { 0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B };

    /// <summary>
    /// Unpacks a .zip (first .nes/.sfc/.smc/.gb/.gbc entry) and returns the ROM bytes and the inner file name. Other
    /// files come back as they are.
    /// </summary>
    public static byte[] Unwrap(byte[] data, string fileName, out string innerName)
    {
        innerName = fileName;
        bool zip = data.Length > 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4;
        if (!zip) return data;
        using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => Consoles.AllRomExtensions.Any(x => x != ".zip" && e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidDataException($"No ROM inside {fileName}");
        innerName = entry.Name;
        using var s = entry.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The console this ROM is for, or null when it looks like none of them.</summary>
    public static ConsoleKind? Detect(byte[] rom, string fileName)
    {
        if (rom.Length >= 16 && rom[0] == 'N' && rom[1] == 'E' && rom[2] == 'S' && rom[3] == 0x1A) return ConsoleKind.Nes;
        if (rom.Length >= 0x150 && rom.AsSpan(0x104, NintendoLogoStart.Length).SequenceEqual(NintendoLogoStart))
            return (rom[0x143] & 0x80) != 0 ? ConsoleKind.GameBoyColor : ConsoleKind.GameBoy;
        // Sega: only while the console is available (a hidden console must not capture ROMs the other paths would have handled)
        if (NesEmulator.Sega.SegaRomFormat.Detect(rom, fileName) is { } sega && Consoles.IsAvailable(sega)) return sega;
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".nes" => ConsoleKind.Nes,
            ".sfc" or ".smc" => ConsoleKind.Snes,
            ".gbc" => ConsoleKind.GameBoyColor,
            ".gb" => ConsoleKind.GameBoy,
            _ => LooksLikeSnes(rom) ? ConsoleKind.Snes : null,
        };
    }

    /// <summary>A SNES internal header (LoROM $7FC0 or HiROM $FFC0, after an optional 512-byte copier header): the
    /// checksum and its complement add up to $FFFF.</summary>
    private static bool LooksLikeSnes(byte[] rom)
    {
        int skip = rom.Length % 1024 == 512 ? 512 : 0;
        foreach (int hdr in new[] { 0x7FC0, 0xFFC0 })
        {
            int a = skip + hdr;
            if (a + 0x20 > rom.Length) continue;
            int complement = rom[a + 0x1C] | rom[a + 0x1D] << 8, checksum = rom[a + 0x1E] | rom[a + 0x1F] << 8;
            if ((complement ^ checksum) == 0xFFFF) return true;
        }
        return false;
    }

    /// <summary>A ROM this console can run: GB and GBC take each other's cartridges.</summary>
    public static bool Runs(ConsoleKind console, ConsoleKind romConsole) =>
        console == romConsole || (Consoles.IsGameBoy(console) && Consoles.IsGameBoy(romConsole));
}

/// <summary>One entry of a core menu: the id stored in settings, what to show, and the family it belongs to.</summary>
public sealed record CoreOption(string Id, string Label, string Family);

/// <summary>
/// The core menus of each console: the console's own family first, then the other consoles' parts that can stand in
/// (the cross-console bridges). On the NES they are ordinary NES cores from <see cref="CoreRegistry"/>, tagged with
/// the family they really come from; on the SNES and Game Boy they are ids a <see cref="IConsoleSession"/> builds.
/// </summary>
public static class CoreCatalog
{
    // NES-board cores that are really another console's part (the mix bridges).
    private static readonly Dictionary<string, string> NesCpuFamily = new(StringComparer.OrdinalIgnoreCase) { ["SNES"] = "SNES", ["SM83"] = "Game Boy" };
    private static readonly Dictionary<string, string> NesPpuFamily = new(StringComparer.OrdinalIgnoreCase) { ["SNES"] = "SNES", ["DMG"] = "Game Boy", ["DMGX"] = "Game Boy", ["DMGS"] = "Game Boy" };
    private static readonly Dictionary<string, string> NesApuFamily = new(StringComparer.OrdinalIgnoreCase) { ["SNES"] = "SNES", ["DMG"] = "Game Boy", ["DMGS"] = "Game Boy" };

    private static readonly Dictionary<string, string> NesBridgeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CPU:SNES"] = "65816 (SNES CPU)", ["CPU:SM83"] = "SM83 (Game Boy CPU)",
        ["PPU:SNES"] = "SNES picture chip", ["PPU:DMG"] = "Game Boy picture chip (160x144)",
        ["PPU:DMGX"] = "Game Boy picture chip, big screen", ["PPU:DMGS"] = "Game Boy picture chip, big screen + layers",
        ["APU:SNES"] = "SNES sound (S-DSP)", ["APU:DMG"] = "Game Boy sound chip", ["APU:DMGS"] = "Game Boy sound chip, 8 channels",
    };

    /// <summary>The family a NES-board core id belongs to ("NES" unless it is a bridge).</summary>
    public static string NesFamilyOf(CoreSlot slot, string id) =>
        (slot switch { CoreSlot.Cpu => NesCpuFamily, CoreSlot.Ppu => NesPpuFamily, _ => NesApuFamily }).TryGetValue(id, out var f) ? f : "NES";

    /// <summary>NES PPU bridges that need the board's cycle-precise PPU stepping (they wrap PPU_FIX-timed cores).</summary>
    public static bool NesPpuNeedsPreciseTiming(string id) => NesPpuFamily.ContainsKey(id);

    private static IEnumerable<string> NesIds(CoreSlot slot) => slot switch
    {
        CoreSlot.Cpu => CoreRegistry.CpuIds, CoreSlot.Ppu => CoreRegistry.PpuIds, _ => CoreRegistry.ApuIds,
    };

    /// <summary>The menu for one slot of one console, own family first (and within the NES family, FIX first).</summary>
    public static IReadOnlyList<CoreOption> Options(ConsoleKind console, CoreSlot slot)
    {
        var list = new List<CoreOption>();
        switch (console)
        {
            case ConsoleKind.Nes:
            {
                var ids = NesIds(slot).ToList();
                string prefix = slot.ToString().ToUpperInvariant();
                foreach (var id in ids.OrderBy(i => i.Equals("FIX", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(i => i, StringComparer.OrdinalIgnoreCase))
                {
                    string fam = NesFamilyOf(slot, id);
                    list.Add(new CoreOption(id, NesBridgeLabels.TryGetValue(prefix + ":" + id, out var l) ? $"{id} - {l}" : id, fam));
                }
                break;
            }
            // Sega: own cores only for now (the bridges to and from the NES parts arrive with the bridge track). Ids follow the plan: the CPU slot names the
            // chip (Z80, M68K), the picture and sound slots name the console (SMS / GG / MD).
            case ConsoleKind.MasterSystem:
            case ConsoleKind.GameGear:
            {
                bool gg = console == ConsoleKind.GameGear;
                string fam = Consoles.Family(console);
                switch (slot)
                {
                    case CoreSlot.Cpu: list.Add(new("Z80", "Z80 (stock clock)", fam)); break;
                    case CoreSlot.Ppu: list.Add(gg ? new("GG", "GG (Game Gear picture chip)", fam) : new("SMS", "SMS (Master System picture chip)", fam)); break;
                    default: list.Add(gg ? new("GG", "GG (SN76489 PSG with stereo)", fam) : new("SMS", "SMS (SN76489 PSG)", fam)); break;
                }
                break;
            }
            case ConsoleKind.Genesis:
                switch (slot)
                {
                    case CoreSlot.Cpu: list.Add(new("M68K", "M68K (Genesis main CPU)", "Genesis")); break;
                    case CoreSlot.Ppu: list.Add(new("MD", "MD (Genesis picture chip)", "Genesis")); break;
                    default: list.Add(new("MD", "MD (Z80 + YM2612 + PSG sound unit)", "Genesis")); break;
                }
                break;
            case ConsoleKind.Snes:
                switch (slot)
                {
                    case CoreSlot.Cpu: list.Add(new("SFC", "SFC (65816)", "SNES")); break;
                    case CoreSlot.Ppu:
                        list.Add(new("SFC", "SFC (SNES picture chip)", "SNES"));
                        foreach (var id in NesPictureChips()) list.Add(new("NES:" + id, NesPictureLabel(id), NesFamilyOf(CoreSlot.Ppu, id)));
                        break;
                    default:
                        list.Add(new("SFC", "SFC (S-SMP + S-DSP)", "SNES"));
                        list.Add(new("HLE", "HLE (silent - no sound, fastest)", "SNES"));
                        foreach (var id in NesSoundChips()) list.Add(new("NES:" + id, NesSoundLabel(id), NesFamilyOf(CoreSlot.Apu, id)));
                        if (CoreRegistry.ApuIds.Contains("DMG", StringComparer.OrdinalIgnoreCase)) list.Add(new("NES:DMG", "Game Boy sound chip (through NES registers)", "Game Boy"));
                        if (CoreRegistry.ApuIds.Contains("DMGS", StringComparer.OrdinalIgnoreCase)) list.Add(new("NES:DMGS", "Game Boy sound chip, 8 channels (through NES registers)", "Game Boy"));
                        break;
                }
                break;
            default:   // Game Boy / Game Boy Color
                switch (slot)
                {
                    case CoreSlot.Cpu:
                        list.Add(new("GB", "SM83 (stock clock)", "Game Boy"));
                        list.Add(new("GB-HALF", "SM83 at half speed", "Game Boy"));
                        list.Add(new("GB-2X", "SM83 at double speed", "Game Boy"));
                        list.Add(new("GB-NESCLOCK", "SM83 at the NES clock (1.79 MHz)", "NES"));
                        list.Add(new("GB-SNESCLOCK", "SM83 at the SNES clock (3.58 MHz)", "SNES"));
                        list.Add(new("65816", "65816 (SNES CPU)", "SNES"));
                        break;
                    case CoreSlot.Ppu:
                        list.Add(new("GB", console == ConsoleKind.GameBoyColor ? "GB (Game Boy Color picture chip)" : "GB (Game Boy picture chip)", "Game Boy"));
                        foreach (var id in NesPictureChips().Where(i => NesFamilyOf(CoreSlot.Ppu, i) != "Game Boy")) list.Add(new("NES:" + id, NesPictureLabel(id), NesFamilyOf(CoreSlot.Ppu, id)));
                        list.Add(new("SNES:SFC", "SFC (SNES picture chip)", "SNES"));
                        break;
                    default:
                        list.Add(new("GB", "GB (Game Boy sound chip)", "Game Boy"));
                        list.Add(new("GBS", "GBS (Game Boy sound chip + 4 channels for the bridges)", "Game Boy"));
                        foreach (var id in NesSoundChips()) list.Add(new("NES:" + id, NesSoundLabel(id), "NES"));
                        if (CoreRegistry.ApuIds.Contains("SNES", StringComparer.OrdinalIgnoreCase)) list.Add(new("NES:SNES", "SNES sound (through NES registers)", "SNES"));
                        break;
                }
                break;
        }
        // Own family first, the others after in a fixed order; order inside a family is kept.
        string own = Consoles.Family(console);
        string[] order = { own, "NES", "SNES", "Game Boy" };
        return list.OrderBy(o => Array.IndexOf(order, o.Family) is var i and >= 0 ? i : 9).ToList();
    }

    /// <summary>The default core of a slot (the first of the own family).</summary>
    public static string Default(ConsoleKind console, CoreSlot slot) => Options(console, slot).FirstOrDefault()?.Id ?? "FIX";

    /// <summary>A stored id if the menu still offers it, otherwise the default.</summary>
    public static string Resolve(ConsoleKind console, CoreSlot slot, string? stored) =>
        stored != null && Options(console, slot).Any(o => o.Id.Equals(stored, StringComparison.OrdinalIgnoreCase)) ? stored : Default(console, slot);

    // NES picture chips a SNES or Game Boy picture can be drawn on (not the ones that bridge back to the source console).
    private static IEnumerable<string> NesPictureChips() =>
        CoreRegistry.PpuIds.Where(i => !i.Equals("SNES", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => NesFamilyOf(CoreSlot.Ppu, i) == "NES" ? 0 : 1)
            .ThenBy(i => i.Equals("FIXS", StringComparison.OrdinalIgnoreCase) ? 0 : i.Equals("FIX", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase);

    private static string NesPictureLabel(string id) => id.ToUpperInvariant() switch
    {
        "FIXS" => "FIXS - NES picture chip with SNES layers + all sprites",
        "DMG" => "DMG - through a NES onto the Game Boy picture chip",
        "DMGX" => "DMGX - through a NES onto the big-screen Game Boy chip",
        "DMGS" => "DMGS - through a NES onto the Game Boy chip with SNES layers",
        _ => $"{id} - NES picture chip",
    };

    private static IEnumerable<string> NesSoundChips() =>
        CoreRegistry.ApuIds.Where(i => NesFamilyOf(CoreSlot.Apu, i) == "NES")
            .OrderBy(i => i.Equals("FIXS", StringComparison.OrdinalIgnoreCase) ? 0 : i.Equals("FIX", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase);

    private static string NesSoundLabel(string id) => id.ToUpperInvariant() switch
    {
        "FIXS" => "FIXS - NES sound, 8 channels (4 pulses, 2 triangles, 2 noises)",
        _ => $"{id} - NES sound",
    };
}

/// <summary>
/// A running SNES or Game Boy game with its chosen cores, behind one shape the apps can drive: frames in, a picture
/// (0xAARRGGBB), stereo sound, a battery save. (The NES keeps its own, older, much richer path in the apps.)
/// </summary>
public interface IConsoleSession : IDisposable
{
    ConsoleKind Console { get; }
    string Title { get; }
    /// <summary>What the chosen cores and chips are, for a status line.</summary>
    string Description { get; }
    double FramesPerSecond { get; }
    void SetPad(int player, PadButtons buttons);
    void RunFrame();
    void Reset();
    /// <summary>The last frame: <see cref="FrameWidth"/> x <see cref="FrameHeight"/> pixels, 0xAARRGGBB.</summary>
    uint[] Frame { get; }
    int FrameWidth { get; }
    int FrameHeight { get; }
    /// <summary>The width the picture should be shown at relative to its height (a 512-wide SNES hi-res frame still
    /// shows at the width of 256).</summary>
    int DisplayWidth { get; }
    int SampleRate { get; }
    /// <summary>Stereo interleaved samples produced since the last call.</summary>
    int ReadSamples(short[] buffer);
    bool HasBattery { get; }
    byte[] ExportSave();
    void ImportSave(byte[] data);
    /// <summary>A stable id of the game (SHA-1 of the ROM, lower-case hex) for save files.</summary>
    string GameId { get; }
    /// <summary>
    /// Swap one core while the game keeps running (BrokenNes 2): memory, video and the rest carry on, the new part picks up
    /// from the current state. False when this swap cannot be done in place (the app then restarts the game on it).
    /// </summary>
    bool TrySwapCore(CoreSlot slot, string id);
}
