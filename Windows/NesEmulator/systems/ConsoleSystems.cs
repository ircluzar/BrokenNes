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
public enum ConsoleKind { Nes, Snes, GameBoy, GameBoyColor, MasterSystem, GameGear, Genesis, WideMess }

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
    // Master System / Game Gear: Track A milestone A1 met (PSG sound; the YM2413 follows in A2): see docs/projects/sega-track-a.md for the evidence.
    // Genesis: Track B2a met (the picture is real: the VDP's renderer, verified against BlastEm; see docs/projects/sega-track-b2a.md); its sound unit is Track B2b's (real YM2612).
    public static readonly bool MasterSystemReady = true, GameGearReady = true, GenesisReady = true;

    public static bool SegaPreview { get; set; } = Environment.GetEnvironmentVariable("BROKENNES_SEGA") == "1";

    /// <summary>
    /// WideMess, the souped-up SNES fantasy console (docs/projects/widemess-plan.md), is offered only by the hosts that turn it on (the desktop app and Workshop at start-up). Lite and the FL plugin
    /// arrive in its milestone 7; until then they leave it off, so their menus, ROM detection and file filters are what they were.
    /// </summary>
    public static bool WideMessEnabled { get; set; }

    public static bool IsAvailable(ConsoleKind k) => k switch
    {
        ConsoleKind.MasterSystem => MasterSystemReady || SegaPreview,
        ConsoleKind.GameGear => GameGearReady || SegaPreview,
        ConsoleKind.Genesis => GenesisReady || SegaPreview,
        ConsoleKind.WideMess => WideMessEnabled,
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
        { ConsoleKind.Nes, ConsoleKind.Snes, ConsoleKind.GameBoy, ConsoleKind.GameBoyColor, ConsoleKind.MasterSystem, ConsoleKind.GameGear, ConsoleKind.Genesis, ConsoleKind.WideMess };

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
        ConsoleKind.WideMess => "WideMess",
        _ => "Game Boy Color",
    };

    /// <summary>Short stable key for settings and storage ("nes", "snes", "gb", "gbc", "sms", "gg", "md", "wms").</summary>
    public static string Key(ConsoleKind k) => k switch
    {
        ConsoleKind.Nes => "nes", ConsoleKind.Snes => "snes", ConsoleKind.GameBoy => "gb",
        ConsoleKind.MasterSystem => "sms", ConsoleKind.GameGear => "gg", ConsoleKind.Genesis => "md", ConsoleKind.WideMess => "wms",
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
        "wms" or "widemess" => ConsoleKind.WideMess,
        _ => ConsoleKind.Nes,
    };

    /// <summary>The core family that is native to a console ("NES", "SNES", "Game Boy", "Master System", "Genesis").</summary>
    public static string Family(ConsoleKind k) => k switch
    {
        ConsoleKind.Nes => "NES", ConsoleKind.Snes => "SNES",
        ConsoleKind.MasterSystem or ConsoleKind.GameGear => "Master System",
        ConsoleKind.Genesis => "Genesis",
        ConsoleKind.WideMess => "WideMess",
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
        ConsoleKind.WideMess => new[] { ".wms" },
        _ => new[] { ".gb", ".gbc" },
    };

    /// <summary>Every ROM extension BrokenNes 2 opens (the Sega ones only once available), plus .zip (a ROM inside is unpacked).</summary>
    public static string[] AllRomExtensions =>
        new[] { ".nes", ".sfc", ".smc", ".gb", ".gbc" }
            .Concat(new[] { ConsoleKind.MasterSystem, ConsoleKind.GameGear, ConsoleKind.Genesis, ConsoleKind.WideMess }.Where(IsAvailable).SelectMany(Extensions))
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
        if (!zip)
        {
            // an archive format the apps cannot open (the browser build has no 7-Zip): say so, rather than failing to recognise a ROM
            if (IsUnsupportedArchive(data, out string kind))
                throw new InvalidDataException($"'{fileName}' is a {kind} archive, which cannot be opened here. Unpack it first (for example with 7-Zip) and open the ROM inside, or put the ROM in a .zip.");
            return data;
        }
        using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => Consoles.AllRomExtensions.Any(x => x != ".zip" && e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidDataException($"No ROM inside {fileName}");
        innerName = entry.Name;
        using var s = entry.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>True for the archive formats the apps cannot open (7z and RAR: six-byte signatures no ROM starts with), by their first bytes; <paramref name="kind"/> names it.</summary>
    public static bool IsUnsupportedArchive(byte[] data, out string kind)
    {
        kind = "";
        if (data.Length > 6 && data[0] == 0x37 && data[1] == 0x7A && data[2] == 0xBC && data[3] == 0xAF && data[4] == 0x27 && data[5] == 0x1C) kind = "7z";
        else if (data.Length > 7 && data[0] == 'R' && data[1] == 'a' && data[2] == 'r' && data[3] == '!' && data[4] == 0x1A && data[5] == 0x07) kind = "RAR";
        return kind.Length > 0;
    }

    /// <summary>The console this ROM is for, or null when it looks like none of them.</summary>
    public static ConsoleKind? Detect(byte[] rom, string fileName)
    {
        // a WideMess game file (its own header, docs/projects/widemess-spec.md 5), only where the console is offered
        if (NesEmulator.Snes.WmsFile.HasMagic(rom) && Consoles.IsAvailable(ConsoleKind.WideMess)) return ConsoleKind.WideMess;
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

    /// <summary>What a file no console recognises is told: its name and size, what BrokenNes plays (by console and file extension, only the consoles that are on), and the one near miss the Sega library has (a 32X cartridge).</summary>
    public static string UnrecognisedMessage(string fileName, int length)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"BrokenNes does not recognise \"{fileName}\" ({length:N0} bytes) as a game for any console it plays.\n\nIt plays:\n");
        foreach (var k in Consoles.All)
        {
            if (k == ConsoleKind.GameBoyColor || !Consoles.IsAvailable(k)) continue;
            var ext = Consoles.Extensions(k);
            if (k == ConsoleKind.GameBoy) ext = ext.Concat(Consoles.Extensions(ConsoleKind.GameBoyColor)).Distinct().ToArray();
            sb.Append($"  - {(k == ConsoleKind.GameBoy ? "Game Boy / Color" : Consoles.DisplayName(k))}: {string.Join(", ", ext)}\n");
        }
        sb.Append("\nor a .zip holding one of them. A file with another extension is read by its contents (a NES header, a Game Boy logo, a Sega header), so a renamed ROM still loads; this one matched none.");
        if (Path.GetExtension(fileName).Equals(".32x", StringComparison.OrdinalIgnoreCase)) sb.Append("\n\nA Sega 32X cartridge needs the 32X's own processors and is not supported.");
        return sb.ToString();
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

    /// <summary>A ROM this console can run: GB and GBC take each other's cartridges; the WideMess runs SNES games (it is a superset of the SNES).</summary>
    public static bool Runs(ConsoleKind console, ConsoleKind romConsole) =>
        console == romConsole || (Consoles.IsGameBoy(console) && Consoles.IsGameBoy(romConsole)) || (console == ConsoleKind.WideMess && romConsole == ConsoleKind.Snes);
}

/// <summary>
/// What a core menu entry is, of the consoles' best cores: <see cref="Stock"/> = the console's most accurate chip, behaving as itself (on another console it shows what that chip can natively);
/// <see cref="Enhanced"/> = the same chip with the cross-console help (more layers, a picture the guest's size, more channel banks; see <c>docs/projects/enhanced-cores.md</c>);
/// <see cref="Other"/> = everything else (the NES gimmick cores, variants, the silent unit).
/// </summary>
public enum CoreGrade { Stock, Enhanced, Other }

/// <summary>The three parts of a core menu, in this order: the console's own best cores (stock, enhanced), the other consoles' best cores (stock, enhanced), then the rest (the NES gimmick cores and other variants, as before).</summary>
public enum CoreSection { Own, OtherConsoles, More }

/// <summary>One entry of a core menu: the id stored in settings, what to show, and the family it belongs to.</summary>
public sealed record CoreOption(string Id, string Label, string Family)
{
    /// <summary>True for a CPU entry that is an instruction-set wall: the chip really runs the game's machine code, which is another processor's, so the game stops almost at once (said in the label; the menus and the HTTP API show it).</summary>
    public bool Wall { get; init; }
    /// <summary>Stock or enhanced for a console's best core (the label says so), Other for the rest.</summary>
    public CoreGrade Grade { get; init; } = CoreGrade.Other;
    /// <summary>The part of the menu the entry sits in (menus draw a heading per section: <see cref="CoreCatalog.SectionTitle"/>).</summary>
    public CoreSection Section { get; init; } = CoreSection.More;
}

/// <summary>
/// Core ids that were renamed or retired, and what a stored pick of one becomes. A retired id is never silently reset to the default: wherever an id can have been stored (the desktop's config.json,
/// Lite's IndexedDB prefs, a NES save state's core type name, a stockpile's machine, a FL project, the HTTP API, a test script) it is moved to its successor through these functions.
/// <list type="bullet">
/// <item><c>DMGX</c> (the Game Boy picture chip with a big screen, PPU_DMGX on PPU_GBX) became <c>DMGS</c>, the enhanced Game Boy picture chip, when the three Game Boy picture chips were merged into stock and enhanced (2026-10-22).</item>
/// <item><c>DMGXI</c> / <c>DMGSI</c> (the inverted-sprite chips retired before) become <c>DMGS</c> as well, with the Game Boy look's sprite inversion switched on (<see cref="InvertsSprites"/>).</item>
/// </list>
/// The NES-board half lives in <see cref="CoreRegistry.MigratePpuId"/> (the board code is compiled without this folder in the old Web build); this class adds the console-menu forms (<c>NES:DMGX</c>).
/// </summary>
public static class CoreIdMigration
{
    /// <summary>A NES-board core id as it is today (<c>DMGX</c> -> <c>DMGS</c>); any other id unchanged.</summary>
    public static string NesBoard(CoreSlot slot, string id) => slot == CoreSlot.Ppu ? CoreRegistry.MigratePpuId(id) : id;

    /// <summary>A console menu id as it is today: <c>NES:DMGX</c> (and the retired <c>NES:DMGXI</c> / <c>NES:DMGSI</c>) -> <c>NES:DMGS</c>, and the NES console's bare ids through <see cref="NesBoard"/>; any other id unchanged.</summary>
    public static string Console(ConsoleKind console, CoreSlot slot, string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        if (console == ConsoleKind.Nes) return NesBoard(slot, id);
        if (slot == CoreSlot.Ppu && id.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) return "NES:" + CoreRegistry.MigratePpuId(id[4..]);
        return id;
    }

    /// <summary>True when the stored pick is one of the retired inverted-sprite chips (<c>DMGXI</c>, <c>DMGSI</c>, with or without <c>NES:</c>): its migration also switches the Game Boy look's sprite inversion on.</summary>
    public static bool InvertsSprites(string? id) => id != null && (id.EndsWith("DMGXI", StringComparison.OrdinalIgnoreCase) || id.EndsWith("DMGSI", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="id"/> is an id this class moves (the caller then stores the new one back).</summary>
    public static bool IsRetired(ConsoleKind console, CoreSlot slot, string? id) => id != null && !string.Equals(Console(console, slot, id), id, StringComparison.Ordinal);

    /// <summary>
    /// One stored pick of a settings store (the desktop's config.json, Lite's IndexedDB prefs) as it must be stored now: unchanged unless it is retired, then its successor,
    /// with <paramref name="moved"/> set (the store is written back) and <paramref name="invertSprites"/> set when the retired chip had its sprites inverted (the Game Boy look's
    /// sprite inversion is switched on in its place). Every app migrates through this one function, which <c>Workshop --enhanced-test</c> checks.
    /// </summary>
    public static string? Migrate(ConsoleKind console, CoreSlot slot, string? stored, ref bool moved, ref bool invertSprites)
    {
        if (stored == null || !IsRetired(console, slot, stored)) return stored;
        moved = true;
        if (InvertsSprites(stored)) invertSprites = true;
        return Console(console, slot, stored);
    }
}

/// <summary>
/// The core menus of each console. Every PPU and APU menu (and the CPU menus, for the order) has three parts (<see cref="CoreSection"/>): the console's own best cores
/// (<see cref="CoreGrade.Stock"/>, then <see cref="CoreGrade.Enhanced"/>), the other consoles' best cores (stock, enhanced; families in the order NES, SNES, Game Boy, Master System, Genesis),
/// then the rest as before (the NES gimmick cores, variants). The labels of the best cores say "(stock)" / "(enhanced)". On the NES they are ordinary NES cores from <see cref="CoreRegistry"/>,
/// tagged with the family they really come from; on the other consoles they are ids an <see cref="IConsoleSession"/> builds. Ids never change for a label: FIXS is FIX enhanced, DMG / DMGS
/// the Game Boy stock / enhanced chips on the NES board, GBS the Game Boy's enhanced sound unit (saved settings, states and FL projects keep working).
/// </summary>
public static class CoreCatalog
{
    // NES-board cores that are really another console's part (the mix bridges).
    private static readonly Dictionary<string, string> NesCpuFamily = new(StringComparer.OrdinalIgnoreCase) { ["SNES"] = "SNES", ["SM83"] = "Game Boy", ["Z80"] = "Master System", ["M68K"] = "Genesis" };
    private static readonly Dictionary<string, string> NesPpuFamily = new(StringComparer.OrdinalIgnoreCase) { ["SNES"] = "SNES", ["SNESS"] = "SNES", ["DMG"] = "Game Boy", ["DMGS"] = "Game Boy", ["SMS"] = "Master System", ["SMSS"] = "Master System", ["MD"] = "Genesis", ["MDS"] = "Genesis" };
    private static readonly Dictionary<string, string> NesApuFamily = new(StringComparer.OrdinalIgnoreCase) { ["SNES"] = "SNES", ["SNESS"] = "SNES", ["DMG"] = "Game Boy", ["DMGS"] = "Game Boy", ["SMS"] = "Master System", ["SMSS"] = "Master System", ["SMSFM"] = "Master System", ["MD"] = "Genesis", ["MDS"] = "Genesis" };

    /// <summary>The NES board's ids of the other consoles' ENHANCED chips (a NES game on them). Only the NES menu offers them: the other consoles reach those chips directly (<see cref="DirectRoutes"/>), never through a NES.</summary>
    private static readonly HashSet<string> NesBoardEnhancedOfOthers = new(StringComparer.OrdinalIgnoreCase) { "SNESS", "SMSS", "MDS" };

    /// <summary>
    /// The routes from a console to another console's ENHANCED chips with no NES in between (docs/projects/enhanced-cores.md 5): the route id the menus store, the family, the name the label gives, and the chip's id on
    /// its own console's menu. A guest's translator fills the chip's layers directly (<c>EnhancedChips.Picture</c>) and its voices play on the chip's banks (<c>EnhancedChips.Sound</c>). A route is offered once its chips
    /// exist (registered in <c>EnhancedChips</c>), and never to the chip's own family (a Master System game's enhanced chip is SMSS on its own menu, not a route).
    /// </summary>
    private static readonly (string Route, string Family, string Name, string Own)[] DirectRoutes =
    {
        ("SNES:SFCS", "SNES", "SNES", "SFCS"),
        ("SMS:SMSS", "Master System", "Master System", "SMSS"),
        ("GG:GGS", "Master System", "Game Gear", "GGS"),
        ("MD:MDS", "Genesis", "Genesis", "MDS"),
    };

    /// <summary>True when the enhanced chips of <paramref name="route"/> exist (the menus offer them).</summary>
    private static bool Built(string route) => NesEmulator.Mix.EnhancedChips.IsRoute(route);

    /// <summary>True when <paramref name="id"/> is a direct route to another console's enhanced chips (<see cref="DirectRoutes"/>) whose chips exist.</summary>
    public static bool IsDirectRoute(string? id) => id != null && Array.Exists(DirectRoutes, r => r.Route.Equals(id, StringComparison.OrdinalIgnoreCase)) && Built(id);

    private static readonly Dictionary<string, string> NesBridgeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CPU:SNES"] = "65816 (SNES CPU) - runs the game's 6502 code in its emulation mode; a game that uses the 6502's unofficial opcodes stops",
        ["CPU:SM83"] = "SM83 (Game Boy CPU) - an instruction-set wall: it runs the game's 6502 code as SM83 instructions, so most games stop at once",
        ["CPU:Z80"] = "Z80 (Master System CPU) - an instruction-set wall: it runs the game's 6502 code as Z80 instructions, so nearly every game stops at once",
        ["CPU:M68K"] = "M68K (Genesis CPU) - an instruction-set wall: it runs the game's 6502 code as 68000 instructions, so nearly every game stops at once",
        ["PPU:FIX"] = "the NES picture chip (2C02), dot-accurate", ["PPU:FIXS"] = "the NES picture chip with the cross-console layers (the guest's picture at its own size with all its layers and sprites; NES colours)",
        ["PPU:SNES"] = "SNES picture chip", ["PPU:DMG"] = "Game Boy picture chip (160x144: the middle of the picture, 4 shades by brightness)",
        ["PPU:DMGS"] = "Game Boy picture chip with the whole picture and the cross-console layers (4 shades by brightness, or Game Boy Color colours)",
        ["PPU:SMS"] = "Master System picture chip (Mode 4: 4-bit tiles, 32 colours of 64, 8 sprites a line; 240-line mode)",
        ["PPU:MD"] = "Genesis picture chip (4-bit tiles, 64 colours of 512, 16 sprites a line, per-line scroll)",
        ["PPU:SNESS"] = "the SNES picture chip with the cross-console layers: the game whole at its own 256 x 240 (the SNES shows 224 or 239 lines), every sprite, in the SNES's 15-bit colours",
        ["PPU:SMSS"] = "the Master System picture chip with the cross-console layers: the game whole at its own 256 x 240 (the Master System shows 192 to 240 lines), every sprite, in the Master System's 64 colours",
        ["PPU:MDS"] = "the Genesis picture chip with the cross-console layers: the game whole at its own 256 x 240, every sprite, in the Genesis's 512 colours",
        ["APU:SNESS"] = "the SNES sound chip with extra S-DSP banks: every NES channel on a voice of its own, pulses with their duty (the DMC is dropped)",
        ["APU:SMSS"] = "the Master System sound chip with extra SN76489 sets: every NES channel on a channel of its own (the DMC is dropped)",
        ["APU:MDS"] = "the Genesis sound chips with extra YM2612 + PSG sets: every NES channel on a channel of its own (the DMC is dropped)",
        ["APU:FIX"] = "the NES sound chip (2A03), accurate", ["APU:FIXS"] = "the NES sound chip with extra channel banks for the cross-console sound (as many as the other console's voices need)",
        ["APU:SNES"] = "SNES sound (S-DSP)", ["APU:DMG"] = "Game Boy sound chip", ["APU:DMGS"] = "Game Boy sound chip with extra channel banks (as many as the other console's voices need)",
        ["APU:SMS"] = "Master System / Game Gear sound chip (SN76489 PSG: 3 square tones + noise; the DMC is dropped)",
        ["APU:SMSFM"] = "Master System FM chip (YM2413: pulses and triangle as FM channels with a chosen instrument, noise on the SN76489 PSG; the DMC is dropped)",
        ["APU:MD"] = "Genesis sound chips (YM2612 FM + SN76489: pulses and triangle as FM voices with a chosen patch, noise on the PSG; the DMC is dropped)",
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

    /// <summary>
    /// The tooltip of a core menu entry on a console other than the NES (the NES shows overlay cards): what the entry is in a line or two, and what it cannot do. The label says what the part is; this adds what the
    /// choice costs: a part from another console is a bridge, and every bridge loses something that the status line and the HTTP API count.
    /// </summary>
    public static string Tip(ConsoleKind console, CoreSlot slot, CoreOption o)
    {
        string name = Consoles.DisplayName(console), own = Consoles.Family(console);
        if (o.Wall) return o.Label;      // an instruction-set wall says so in its label: the game stops almost at once
        string grade = o.Grade switch
        {
            CoreGrade.Enhanced when slot == CoreSlot.Ppu => "\nEnhanced: the chip takes another console's layers and its whole picture (as many layers as the game uses, the game's own picture size), within the chip's own colours.",
            CoreGrade.Enhanced => "\nEnhanced: the chip adds channel banks for another console's voices, so they are not squeezed into its own channels.",
            CoreGrade.Stock when o.Family != own => $"\nStock: the {o.Family}'s chip as it is, showing what it natively can.",
            _ => "",
        };
        if (o.Family == own) return $"{o.Label}\nThe {name}'s own {slot.ToString().ToUpperInvariant()} part: the game runs on it as it was made.{grade}";
        string chip = o.Id.Contains(':') ? o.Id[(o.Id.IndexOf(':') + 1)..] : o.Id;
        return grade.Length > 0 ? TipBody(console, slot, o, name, chip) + grade : TipBody(console, slot, o, name, chip);
    }

    private static string TipBody(ConsoleKind console, CoreSlot slot, CoreOption o, string name, string chip)
    {
        return slot switch
        {
            CoreSlot.Ppu => $"{o.Label}\nDraws the {name} picture with this {o.Family} picture part through a picture bridge. What the part cannot show (colours past its palette, more tiles than it holds, shading) is counted: the status line and GET /api/console list the losses.",
            CoreSlot.Apu when o.Id.StartsWith("NES-FM:", StringComparison.OrdinalIgnoreCase) => $"{o.Label}\nPlays the Master System's PSG and its FM chip (the YM2413) as notes on the NES {chip} sound part. Voices the part cannot hold are dropped and counted; the chips' own timbres are approximated.",
            CoreSlot.Apu => $"{o.Label}\nPlays the {name}'s sound chips as notes on this {o.Family} sound part (the {chip} chip). Voices it cannot hold are dropped and counted; the {name}'s own timbres are approximated, not reproduced.",
            _ => $"{o.Label}\nRuns the game's code on another console's processor: this works only where the two instruction sets agree.",
        };
    }

    /// <summary>
    /// The menu for one slot of one console: the console's own best cores (stock, enhanced), the other consoles' best cores (stock, enhanced; NES, SNES, Game Boy, Master System, Genesis),
    /// then everything else in the order it always had (see <see cref="CoreSection"/>). The first entry is the console's own stock core, the default.
    /// </summary>
    public static IReadOnlyList<CoreOption> Options(ConsoleKind console, CoreSlot slot)
    {
        var key = (console, slot);
        lock (cache) if (cache.TryGetValue(key, out var hit)) return hit;
        var arranged = Arrange(console, slot, RawOptions(console, slot));
        lock (cache) cache[key] = arranged;
        return arranged;
    }

    // The menus are built once per console and slot: CoreRegistry's ids are fixed for the process, and the menus are read on every swap and status request.
    private static readonly Dictionary<(ConsoleKind, CoreSlot), IReadOnlyList<CoreOption>> cache = new();

    /// <summary>The heading a menu draws over a section (desktop menu headings, Lite option groups).</summary>
    public static string SectionTitle(ConsoleKind console, CoreSection section) => section switch
    {
        CoreSection.Own => $"{Consoles.DisplayName(console)} (its own)",
        CoreSection.OtherConsoles => "From the other consoles",
        _ => console == ConsoleKind.Nes ? "More NES cores" : "More (NES cores and variants)",
    };

    /// <summary>
    /// The best cores of each console family, as the NES board knows them (the ids the other consoles reach through "NES:"): the stock chip, then the enhanced one where it exists. The SNES, Master System
    /// and Genesis have stock chips only for now; their enhanced chips attach later through the same contract (docs/projects/enhanced-cores.md).
    /// </summary>
    private static readonly (string Family, CoreSlot Slot, string NesId, CoreGrade Grade, string Name)[] BestOnNes =
    {
        ("NES", CoreSlot.Cpu, "FIX", CoreGrade.Stock, "FIX"),
        ("SNES", CoreSlot.Cpu, "SNES", CoreGrade.Stock, "SNES"),
        ("Game Boy", CoreSlot.Cpu, "SM83", CoreGrade.Stock, "Game Boy"),
        ("Master System", CoreSlot.Cpu, "Z80", CoreGrade.Stock, "Master System"),
        ("Genesis", CoreSlot.Cpu, "M68K", CoreGrade.Stock, "Genesis"),
        ("NES", CoreSlot.Ppu, "FIX", CoreGrade.Stock, "FIX"), ("NES", CoreSlot.Ppu, "FIXS", CoreGrade.Enhanced, "FIX"),
        ("SNES", CoreSlot.Ppu, "SNES", CoreGrade.Stock, "SNES"), ("SNES", CoreSlot.Ppu, "SNESS", CoreGrade.Enhanced, "SNES"),
        ("Game Boy", CoreSlot.Ppu, "DMG", CoreGrade.Stock, "Game Boy"), ("Game Boy", CoreSlot.Ppu, "DMGS", CoreGrade.Enhanced, "Game Boy"),
        ("Master System", CoreSlot.Ppu, "SMS", CoreGrade.Stock, "Master System"), ("Master System", CoreSlot.Ppu, "SMSS", CoreGrade.Enhanced, "Master System"),
        ("Genesis", CoreSlot.Ppu, "MD", CoreGrade.Stock, "Genesis"), ("Genesis", CoreSlot.Ppu, "MDS", CoreGrade.Enhanced, "Genesis"),
        ("NES", CoreSlot.Apu, "FIX", CoreGrade.Stock, "FIX"), ("NES", CoreSlot.Apu, "FIXS", CoreGrade.Enhanced, "FIX"),
        ("SNES", CoreSlot.Apu, "SNES", CoreGrade.Stock, "SNES"), ("SNES", CoreSlot.Apu, "SNESS", CoreGrade.Enhanced, "SNES"),
        ("Game Boy", CoreSlot.Apu, "DMG", CoreGrade.Stock, "Game Boy"), ("Game Boy", CoreSlot.Apu, "DMGS", CoreGrade.Enhanced, "Game Boy"),
        ("Master System", CoreSlot.Apu, "SMS", CoreGrade.Stock, "Master System"), ("Master System", CoreSlot.Apu, "SMSFM", CoreGrade.Stock, "Master System FM"),
        ("Master System", CoreSlot.Apu, "SMSS", CoreGrade.Enhanced, "Master System"),
        ("Genesis", CoreSlot.Apu, "MD", CoreGrade.Stock, "Genesis"), ("Genesis", CoreSlot.Apu, "MDS", CoreGrade.Enhanced, "Genesis"),
    };

    /// <summary>A console's own best cores by their own menu ids (stock, then enhanced).</summary>
    private static (string Id, CoreGrade Grade, string Name)[] OwnBest(ConsoleKind console, CoreSlot slot)
    {
        var stock = (console, slot) switch
        {
            (ConsoleKind.Nes, _) => BestOnNes.Where(b => b.Family == "NES" && b.Slot == slot).Select(b => (b.NesId, b.Grade, b.Name)).ToArray(),
            (ConsoleKind.Snes, _) => new[] { ("SFC", CoreGrade.Stock, "SNES") },
            (ConsoleKind.GameBoy or ConsoleKind.GameBoyColor, CoreSlot.Apu) => new[] { ("GB", CoreGrade.Stock, "Game Boy"), ("GBS", CoreGrade.Enhanced, "Game Boy") },
            (ConsoleKind.GameBoy or ConsoleKind.GameBoyColor, _) => new[] { ("GB", CoreGrade.Stock, console == ConsoleKind.GameBoyColor ? "Game Boy Color" : "Game Boy") },
            (ConsoleKind.MasterSystem, CoreSlot.Cpu) or (ConsoleKind.GameGear, CoreSlot.Cpu) => new[] { ("Z80", CoreGrade.Stock, Consoles.DisplayName(console)) },
            (ConsoleKind.MasterSystem, CoreSlot.Apu) => new[] { ("SMS", CoreGrade.Stock, "Master System"), ("SMS-FM", CoreGrade.Stock, "Master System FM") },
            (ConsoleKind.MasterSystem, _) => new[] { ("SMS", CoreGrade.Stock, "Master System") },
            (ConsoleKind.GameGear, _) => new[] { ("GG", CoreGrade.Stock, "Game Gear") },
            (ConsoleKind.Genesis, CoreSlot.Cpu) => new[] { ("M68K", CoreGrade.Stock, "Genesis") },
            (ConsoleKind.WideMess, CoreSlot.Apu) => new[] { ("WMS", CoreGrade.Stock, "WideMess"), ("SFC", CoreGrade.Stock, "SNES") },
            (ConsoleKind.WideMess, _) => new[] { ("WMS", CoreGrade.Stock, "WideMess") },
            _ => new[] { ("MD", CoreGrade.Stock, "Genesis") },
        };
        // the console's own enhanced picture and sound chips (SFCS, SMSS, GGS, MDS), once they exist: after its stock cores
        if (slot == CoreSlot.Cpu || console == ConsoleKind.Nes) return stock;
        foreach (var r in DirectRoutes)
            if (r.Family == Consoles.Family(console) && Built(r.Route) && OwnRouteOf(console) == r.Route)
                return stock.Append((r.Own, CoreGrade.Enhanced, r.Name)).ToArray();
        return stock;
    }

    /// <summary>The direct route whose chips are this console's own (the Master System's SMS:SMSS, the Game Gear's GG:GGS), or null.</summary>
    private static string? OwnRouteOf(ConsoleKind console) => console switch
    {
        ConsoleKind.Snes => "SNES:SFCS", ConsoleKind.MasterSystem => "SMS:SMSS", ConsoleKind.GameGear => "GG:GGS", ConsoleKind.Genesis => "MD:MDS", _ => null,
    };

    private static readonly string[] FamilyOrder = { "NES", "SNES", "Game Boy", "Master System", "Genesis" };

    /// <summary>The NES-board chip an entry reaches (its id without the route: "NES:FIXS" -> FIXS, "NES-FM:FIX" -> FIX, "SNES:SFC" -> SNES), and whether that is a best core of another family.</summary>
    private static (string Family, CoreGrade Grade, string Name)? CrossBest(ConsoleKind console, CoreSlot slot, CoreOption o)
    {
        string id = o.Id;
        if (console == ConsoleKind.Nes)
        {
            foreach (var b in BestOnNes) if (b.Slot == slot && b.Family != "NES" && b.NesId.Equals(id, StringComparison.OrdinalIgnoreCase)) return (b.Family, b.Grade, b.Name);
            return null;
        }
        // the Game Boy draws on the SNES chip directly (GbToSnes, the Super Game Boy idea): the SNES's stock picture chip seen from the Game Boy
        if (id.Equals("SNES:SFC", StringComparison.OrdinalIgnoreCase)) return ("SNES", CoreGrade.Stock, "SNES");
        // another console's enhanced chips, reached directly (no NES in between)
        foreach (var r in DirectRoutes)
            if (r.Route.Equals(id, StringComparison.OrdinalIgnoreCase) && r.Family != Consoles.Family(console)) return (r.Family, CoreGrade.Enhanced, r.Name);
        int colon = id.IndexOf(':');
        if (colon < 0) return null;
        string route = id[..colon], nes = id[(colon + 1)..];
        if (!route.Equals("NES", StringComparison.OrdinalIgnoreCase) && !route.Equals("NES-FM", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var b in BestOnNes)
            if (b.Slot == slot && b.NesId.Equals(nes, StringComparison.OrdinalIgnoreCase) && b.Family != Consoles.Family(console))
                return (b.Family, b.Grade, route.Equals("NES-FM", StringComparison.OrdinalIgnoreCase) ? b.Name + " with the FM chip" : b.Name);
        return null;
    }

    /// <summary>"FIX (enhanced) - ..." from a label that may start with its id ("FIXS - NES picture chip ..."): the grade's name first, then what the entry does.</summary>
    private static string GradedLabel(string name, CoreGrade grade, CoreOption o)
    {
        string label = o.Label, id = o.Id, bare = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id;
        foreach (var head in new[] { id + " - ", bare + " - ", id + " (", bare + " (" })
            if (label.StartsWith(head, StringComparison.OrdinalIgnoreCase)) { label = head.EndsWith("(") ? label[(head.Length - 1)..].Trim() : label[head.Length..]; break; }
        if (label.Equals(id, StringComparison.OrdinalIgnoreCase) || label.Equals(bare, StringComparison.OrdinalIgnoreCase)) label = "";
        // "(SNES picture chip)" left from "SFC (SNES picture chip)": drop the brackets
        if (label.StartsWith("(") && label.EndsWith(")")) label = label[1..^1];
        string head2 = $"{name} ({(grade == CoreGrade.Enhanced ? "enhanced" : "stock")})";
        return label.Length > 0 ? $"{head2} - {label}" : head2;
    }

    /// <summary>Puts a console's menu in its three sections and names the best cores stock / enhanced (the rest keep their label and their order).</summary>
    private static IReadOnlyList<CoreOption> Arrange(ConsoleKind console, CoreSlot slot, List<CoreOption> raw)
    {
        var own = new List<CoreOption>(); var cross = new List<(int family, int order, CoreOption o)>(); var more = new List<CoreOption>();
        var ownBest = OwnBest(console, slot);
        for (int i = 0; i < raw.Count; i++)
        {
            var o = raw[i];
            int ob = Array.FindIndex(ownBest, b => b.Id.Equals(o.Id, StringComparison.OrdinalIgnoreCase));
            if (ob >= 0)
            {
                var (_, grade, name) = ownBest[ob];
                own.Add(o with { Label = slot == CoreSlot.Cpu ? o.Label : GradedLabel(name, grade, o), Grade = grade, Section = CoreSection.Own });
                continue;
            }
            if (CrossBest(console, slot, o) is { } x)
            {
                cross.Add((Array.IndexOf(FamilyOrder, x.Family), i, o with { Label = slot == CoreSlot.Cpu ? o.Label : GradedLabel(x.Name, x.Grade, o), Grade = x.Grade, Section = CoreSection.OtherConsoles }));
                continue;
            }
            more.Add(o with { Grade = CoreGrade.Other, Section = CoreSection.More });
        }
        // own: in the order of OwnBest (stock first); the others: by family, then stock before enhanced, then the order they came in (the FM route after the plain one)
        own.Sort((a, b) => Array.FindIndex(ownBest, x => x.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase)).CompareTo(Array.FindIndex(ownBest, x => x.Id.Equals(b.Id, StringComparison.OrdinalIgnoreCase))));
        var crossSorted = cross.OrderBy(c => c.family).ThenBy(c => c.o.Id.StartsWith("NES-FM:", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenBy(c => c.o.Grade).ThenBy(c => c.order).Select(c => c.o);
        return own.Concat(crossSorted).Concat(more).ToList();
    }

    /// <summary>The console's own enhanced chip (once it exists) and the routes to the other consoles' enhanced chips (once they exist), for the picture or the sound slot.</summary>
    private static void AddEnhanced(List<CoreOption> list, ConsoleKind console, CoreSlot slot)
    {
        // the WideMess's chips on the other consoles' menus, and theirs on its, are its milestone 7
        if (slot == CoreSlot.Cpu || console == ConsoleKind.Nes || console == ConsoleKind.WideMess) return;
        string own = Consoles.Family(console);
        if (OwnRouteOf(console) is { } mine && Built(mine))
        {
            var r = Array.Find(DirectRoutes, d => d.Route == mine);
            list.Add(new(r.Own, OwnEnhancedLabel(console, r.Own, slot), own));
        }
        foreach (var r in DirectRoutes)
            if (r.Family != own && Built(r.Route)) list.Add(new(r.Route, RouteLabel(r.Route, slot), r.Family));
    }

    private static string OwnEnhancedLabel(ConsoleKind console, string id, CoreSlot slot)
    {
        string name = Consoles.DisplayName(console);
        return slot == CoreSlot.Ppu
            ? $"{id} (a {name} game draws exactly as on the stock chip; the chip also takes another console's picture at its own size with all its layers)"
            : $"{id} (the stock sound for a {name} game, plus extra banks of its channels for another console's voices)";
    }

    private static string RouteLabel(string route, CoreSlot slot) => (route.ToUpperInvariant(), slot) switch
    {
        ("SNES:SFCS", CoreSlot.Ppu) => "SFCS - SNES picture chip with the game's layers at its own size (15-bit colours, the colour math in them; no NES in between)",
        ("SNES:SFCS", _) => "SFCS - SNES sound with S-DSP banks: every voice on a DSP voice of its own (looped samples at their pitch, the pan kept)",
        ("SMS:SMSS", CoreSlot.Ppu) => "SMSS - Master System picture chip with the game's layers at its own size (the Master System's 64 colours)",
        ("SMS:SMSS", _) => "SMSS - Master System sound with SN76489 sets: every voice on a channel of its own (squares and noise; mono)",
        ("GG:GGS", CoreSlot.Ppu) => "GGS - Game Gear picture chip with the game's layers at its own size (the Game Gear's 4,096 colours)",
        ("GG:GGS", _) => "GGS - Game Gear sound with SN76489 sets: every voice on a channel of its own, the pan on the stereo port",
        ("MD:MDS", CoreSlot.Ppu) => "MDS - Genesis picture chip with the game's layers at its own size (512 colours, shadow and highlight for the colour math)",
        _ => "MDS - Genesis sound with YM2612 + PSG sets: every voice on a channel of its own (FM voices, PSG squares and noise, the pan on the FM)",
    };

    /// <summary>Every entry of a console's menu with the labels the bridges give them (unordered sections: <see cref="Arrange"/> orders them).</summary>
    private static List<CoreOption> RawOptions(ConsoleKind console, CoreSlot slot)
    {
        var list = new List<CoreOption>();
        AddEnhanced(list, console, slot);
        switch (console)
        {
            case ConsoleKind.Nes:
            {
                var ids = NesIds(slot).ToList();
                string prefix = slot.ToString().ToUpperInvariant();
                foreach (var id in ids.OrderBy(i => i.Equals("FIX", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(i => i, StringComparer.OrdinalIgnoreCase))
                {
                    string fam = NesFamilyOf(slot, id);
                    list.Add(new CoreOption(id, NesBridgeLabels.TryGetValue(prefix + ":" + id, out var l) ? $"{id} - {l}" : id, fam) { Wall = slot == CoreSlot.Cpu && id.ToUpperInvariant() is "Z80" or "M68K" or "SM83" });
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
                    case CoreSlot.Ppu:
                        list.Add(gg ? new("GG", "GG (Game Gear picture chip)", fam) : new("SMS", "SMS (Master System picture chip)", fam));
                        // The game's picture drawn by a NES chip (SmsToNes, picture id "NES:<chip>"), or through the NES by a Game Boy / SNES / Genesis chip. Lossy and said so (the losses are counted).
                        foreach (var id in SegaPictureChips(fam)) list.Add(new("NES:" + id, SegaPictureLabel(id), NesFamilyOf(CoreSlot.Ppu, id)));
                        break;
                    default:
                        list.Add(gg ? new("GG", "GG (SN76489 PSG with stereo)", fam) : new("SMS", "SMS (SN76489 PSG)", fam));
                        // The Japanese Master System / Mark III also has the YM2413 FM chip (ports $F0-$F2); a game that detects it plays its FM music. Not the default: the export console has none.
                        if (!gg) list.Add(new("SMS-FM", "SMS-FM (SN76489 PSG + YM2413 FM, the Japanese console)", fam));
                        // The game's PSG replayed on a NES sound chip (SmsSoundOnNes, unit id "NES:<chip>"). Lossy and said so: mono, PSG only.
                        foreach (var id in NesSoundChips()) list.Add(new("NES:" + id, SegaOnNesLabel(id), "NES"));
                        // The Japanese console's PSG and its YM2413 together on a NES chip (SmsFmSoundOnNes, unit id "NES-FM:<chip>"): the game finds the FM chip and plays its FM music; each FM voice becomes a pulse, a triangle or a noise. Master System only (the Game Gear has no FM).
                        if (!gg) foreach (var id in NesSoundChips().Where(i => i.Equals("FIX", StringComparison.OrdinalIgnoreCase) || i.Equals("FIXS", StringComparison.OrdinalIgnoreCase))) list.Add(new("NES-FM:" + id, SegaFmOnNesLabel(id), "NES"));
                        // ... and, through the NES registers, on another console's sound chip (the Game Boy, SNES and Genesis ones; the Master System's own are this console's).
                        foreach (var id in HubSoundChips(fam)) list.Add(new("NES:" + id, HubSoundLabel(id, "the Master System's PSG"), NesFamilyOf(CoreSlot.Apu, id)));
                        break;
                }
                break;
            }
            case ConsoleKind.Genesis:
                switch (slot)
                {
                    case CoreSlot.Cpu: list.Add(new("M68K", "M68K (Genesis main CPU)", "Genesis")); break;
                    case CoreSlot.Ppu:
                        list.Add(new("MD", "MD (Genesis picture chip)", "Genesis"));
                        foreach (var id in SegaPictureChips("Genesis")) list.Add(new("NES:" + id, SegaPictureLabel(id), NesFamilyOf(CoreSlot.Ppu, id)));
                        break;
                    default:
                        list.Add(new("MD", "MD (Z80 + YM2612 + PSG sound unit)", "Genesis"));
                        // The game's FM, DAC and PSG replayed on a NES sound chip (MdSoundOnNes, unit id "NES:<chip>"). Lossy and said so. WF and MNES are left out: they play a NES game's notes as MIDI through a
                        // SoundFont rather than emulating a chip, so a Genesis game's voices would reach no sound there (the verifier checks that every chip listed here sounds).
                        foreach (var id in NesSoundChips().Where(i => !i.Equals("WF", StringComparison.OrdinalIgnoreCase) && !i.Equals("MNES", StringComparison.OrdinalIgnoreCase))) list.Add(new("NES:" + id, GenesisOnNesLabel(id), "NES"));
                        foreach (var id in HubSoundChips("Genesis")) list.Add(new("NES:" + id, HubSoundLabel(id, "the Genesis's FM, DAC and PSG"), NesFamilyOf(CoreSlot.Apu, id)));
                        break;
                }
                break;
            // WideMess (docs/projects/widemess-spec.md): its own parts only for now. The CPU is the SNES's 65816 (its fast mode arrives in milestone 4), the picture chip is the
            // WideMess's (the SNES chip + 512 KB video RAM, 1,024 colours, 8 layers of 256 colours, 400 wide), the sound chip is the WideMess's (the SNES's S-SMP + S-DSP + voices
            // 8-31 and 1 MB of sound RAM, spec section 4), with the SNES's own as the alternative.
            case ConsoleKind.WideMess:
                switch (slot)
                {
                    case CoreSlot.Cpu: list.Add(new("WMS", "WMS (65816; the fast mode arrives in milestone 4)", "WideMess")); break;
                    case CoreSlot.Ppu: list.Add(new("WMS", "WMS (WideMess picture chip: the SNES chip + 512 KB video RAM, 1,024 colours, 8 layers of 256 colours, 400 wide)", "WideMess")); break;
                    default:
                        list.Add(new("WMS", "WMS (WideMess sound chip: the S-SMP + S-DSP, plus 24 more voices in BRR or 16-bit PCM and 1 MB of sound RAM once a game unlocks them)", "WideMess"));
                        list.Add(new("SFC", "SFC (the SNES's S-SMP + S-DSP: 8 voices, 64 KB, no WideMess sound)", "WideMess"));
                        list.Add(new("HLE", "HLE (silent - no sound, fastest)", "WideMess"));
                        break;
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
                        if (CoreRegistry.ApuIds.Contains("DMGS", StringComparer.OrdinalIgnoreCase)) list.Add(new("NES:DMGS", "Game Boy sound chip with extra channel banks: all eight of the SNES's voices (through NES registers)", "Game Boy"));
                        foreach (var id in HubSoundChips("SNES").Where(i => NesFamilyOf(CoreSlot.Apu, i) != "Game Boy")) list.Add(new("NES:" + id, HubSoundLabel(id, "the SNES's voices"), NesFamilyOf(CoreSlot.Apu, id)));
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
                        list.Add(new("65816", "65816 (SNES CPU) - an instruction-set wall: it runs the game's SM83 code as 65816 instructions, so nearly every game stops at the boot logo", "SNES") { Wall = true });
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
                        foreach (var id in HubSoundChips("Game Boy").Where(i => NesFamilyOf(CoreSlot.Apu, i) != "SNES")) list.Add(new("NES:" + id, HubSoundLabel(id, "the Game Boy's channels"), NesFamilyOf(CoreSlot.Apu, id)));
                        break;
                }
                break;
        }
        // Own family first, the others after in a fixed order; order inside a family is kept (the "More" section keeps this order).
        string own = Consoles.Family(console);
        string[] order = { own, "NES", "SNES", "Game Boy" };
        return list.OrderBy(o => Array.IndexOf(order, o.Family) is var i and >= 0 ? i : 9).ToList();
    }

    /// <summary>The default core of a slot (the console's own stock core, the first entry).</summary>
    public static string Default(ConsoleKind console, CoreSlot slot) => Options(console, slot).FirstOrDefault()?.Id ?? "FIX";

    /// <summary>A stored id if the menu still offers it (a retired id moved to its successor first: <see cref="CoreIdMigration"/>), otherwise the default.</summary>
    public static string Resolve(ConsoleKind console, CoreSlot slot, string? stored)
    {
        if (stored == null) return Default(console, slot);
        string id = CoreIdMigration.Console(console, slot, stored);
        return Options(console, slot).Any(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ? id : Default(console, slot);
    }

    /// <summary>The menu entry for an id (a retired id finds its successor), or null when the menu does not offer it.</summary>
    public static CoreOption? Find(ConsoleKind console, CoreSlot slot, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        string now = CoreIdMigration.Console(console, slot, id);
        return Options(console, slot).FirstOrDefault(o => o.Id.Equals(now, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when a picture chip id of this console draws through a Game Boy picture chip (the NES board's DMG / DMGS, or "NES:DMG" / "NES:DMGS" on another console) - or is the Game Boy's own: the Game Boy look and its inversions apply.</summary>
    public static bool IsGameBoyPicture(ConsoleKind console, string? ppuId)
    {
        if (Consoles.IsGameBoy(console)) return true;
        if (string.IsNullOrEmpty(ppuId)) return false;
        string id = CoreIdMigration.Console(console, CoreSlot.Ppu, ppuId);
        if (console != ConsoleKind.Nes)
        {
            if (!id.StartsWith("NES:", StringComparison.OrdinalIgnoreCase)) return false;
            id = id[4..];
        }
        return NesFamilyOf(CoreSlot.Ppu, id) == "Game Boy";
    }

    // NES picture chips a SNES or Game Boy picture can be drawn on (not the ones that bridge back to the source console).
    private static IEnumerable<string> NesPictureChips() =>
        CoreRegistry.PpuIds.Where(i => !i.Equals("SNES", StringComparison.OrdinalIgnoreCase) && !NesBoardEnhancedOfOthers.Contains(i))
            .OrderBy(i => NesFamilyOf(CoreSlot.Ppu, i) == "NES" ? 0 : 1)
            .ThenBy(i => i.Equals("FIXS", StringComparison.OrdinalIgnoreCase) ? 0 : i.Equals("FIX", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase);

    // The NES picture chips a Sega game can be drawn on: every NES-board picture chip but the console family's own (a Master System game on the Master System chip is the console itself).
    private static IEnumerable<string> SegaPictureChips(string ownFamily) =>
        CoreRegistry.PpuIds.Where(i => NesFamilyOf(CoreSlot.Ppu, i) != ownFamily && !NesBoardEnhancedOfOthers.Contains(i))
            .OrderBy(i => NesFamilyOf(CoreSlot.Ppu, i) == "NES" ? 0 : 1)
            .ThenBy(i => i.Equals("FIXS", StringComparison.OrdinalIgnoreCase) ? 0 : i.Equals("FIX", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase);

    /// <summary>The menu label of a Sega game's picture drawn by a NES (or, through it, another console's) chip. Honest about the route and what it costs.</summary>
    private static string SegaPictureLabel(string id) => id.ToUpperInvariant() switch
    {
        "FIXS" => "FIXS - NES picture chip with the game's layers at the console's own size (the Genesis's whole 320 columns; per-line scroll, tiles in 3 colours + clear, 8 palettes, sprites in the VDP's order; losses counted)",
        "SNES" => "SNES - through a NES onto the SNES picture chip",
        "DMG" or "DMGS" or "SMS" or "MD" => NesPictureLabel(id),
        _ => $"{id} - NES picture chip (flat: one 256-tile background, 4 palettes of 3 colours, 64 sprites; losses counted)",
    };

    private static string NesPictureLabel(string id) => id.ToUpperInvariant() switch
    {
        "SMS" => "SMS - through a NES onto the Master System picture chip",
        "MD" => "MD - through a NES onto the Genesis picture chip",
        "FIXS" => "FIXS - NES picture chip with the game's layers + all sprites, at the game's own size",
        "FIX" => "FIX - NES picture chip (flat: one background layer)",
        "DMG" => "DMG - through a NES onto the Game Boy picture chip (the middle 160x144, 4 shades by brightness)",
        "DMGS" => "DMGS - through a NES onto the Game Boy picture chip with the game's whole picture at its own size and its layers",
        _ => $"{id} - NES picture chip",
    };

    /// <summary>The sound chips of other consoles that are NES APU cores (the bridges: Game Boy, SNES, Master System, Genesis), minus <paramref name="ownFamily"/>'s: a game's sound reaches them through the NES registers, which is how every foreign sound path works.</summary>
    private static IEnumerable<string> HubSoundChips(string ownFamily) =>
        CoreRegistry.ApuIds.Where(i => NesFamilyOf(CoreSlot.Apu, i) is var f && f != "NES" && f != ownFamily && !NesBoardEnhancedOfOthers.Contains(i))
            .OrderBy(i => Array.IndexOf(new[] { "Game Boy", "SNES", "Master System", "Genesis" }, NesFamilyOf(CoreSlot.Apu, i)))
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase);

    /// <summary>The menu label of a Sega game's (or a Game Boy / SNES game's) sound played by another console's chip through the NES registers. Says what the chip is and what is carried; the losses of the NES hop are counted by the bridge.</summary>
    private static string HubSoundLabel(string id, string carried) => id.ToUpperInvariant() switch
    {
        "DMG" => $"DMG - through a NES onto the Game Boy sound chip ({carried} as 2 pulses, wave and noise; mono)",
        "DMGS" => $"DMGS - through a NES onto the Game Boy sound chip with extra channel banks ({carried}, every voice on a channel of its own; mono)",
        "SNES" => $"SNES - through a NES onto the SNES sound (S-DSP; {carried} as sampled voices)",
        "SMS" => $"SMS - through a NES onto the Master System sound chip ({carried} on the SN76489's 3 tones + noise)",
        "SMSFM" => $"SMSFM - through a NES onto the Master System FM chip ({carried} on YM2413 instruments + the PSG's noise)",
        "MD" => $"MD - through a NES onto the Genesis sound chips ({carried} on YM2612 voices + PSG)",
        _ => $"{id} - through a NES onto another console's sound chip",
    };

    private static IEnumerable<string> NesSoundChips() =>
        CoreRegistry.ApuIds.Where(i => NesFamilyOf(CoreSlot.Apu, i) == "NES")
            .OrderBy(i => i.Equals("FIXS", StringComparison.OrdinalIgnoreCase) ? 0 : i.Equals("FIX", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase);

    /// <summary>The menu label of a Master System / Game Gear game's sound played by a NES chip. Honest about what the trip loses: the PSG alone, mono.</summary>
    private static string SegaOnNesLabel(string id) => id.ToUpperInvariant() switch
    {
        "FIXS" => "FIXS - the PSG on the NES chip with extra channel banks (mono; no YM2413 FM, no Game Gear stereo)",
        _ => $"{id} - the PSG on a NES sound chip (mono; no YM2413 FM, no Game Gear stereo)",
    };

    /// <summary>The menu label of the Japanese console's PSG + YM2413 played by a NES chip: says what is kept (the chip is detected, the music is the FM music) and what is lost (the timbre, voices beyond the chip's channels).</summary>
    private static string SegaFmOnNesLabel(string id) => id.ToUpperInvariant() switch
    {
        "FIXS" => "FIXS - the Japanese console's PSG + FM on the NES chip with extra channel banks (FM found by the game; mono; all 18 voices on 7 banks of channels; every FM voice a plain pulse)",
        _ => $"{id} - the Japanese console's PSG + FM on a NES sound chip (FM found by the game; mono; 3 tonal voices + 1 noise of 18, the rest dropped and counted; every FM voice a plain pulse)",
    };

    /// <summary>The menu label of a Genesis game's sound played by a NES chip. Honest about what the trip loses: FM timbre (every FM note is a plain pulse), the DAC's samples (noise hits), panning, and voices beyond the chip's channels.</summary>
    private static string GenesisOnNesLabel(string id) => id.ToUpperInvariant() switch
    {
        "FIXS" => "FIXS - FM, DAC and PSG on the NES chip with extra channel banks (mono; all 11 voices on 5 banks of channels; no FM timbre, the DAC becomes noise hits)",
        _ => $"{id} - FM, DAC and PSG on a NES sound chip (mono; 3 tones + 1 noise; no FM timbre, the DAC becomes noise hits)",
    };

    private static string NesSoundLabel(string id) => id.ToUpperInvariant() switch
    {
        "FIXS" => "FIXS - NES sound with extra channel banks (2 pulses, a triangle and a noise each, as many as the other console's voices need)",
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

    /// <summary>
    /// Why <see cref="TrySwapCore"/> refused (or would refuse) to change <paramref name="slot"/> to <paramref name="id"/> in place: words fit for a message, so the app can tell the user why the game restarts on the new core
    /// instead of swapping it live. The default says only that this session cannot; a session that knows the real reason (a chip whose state cannot move between units, say) overrides it. Never empty.
    /// </summary>
    string SwapRefusal(CoreSlot slot, string id) => $"the {Consoles.DisplayName(Console)} session cannot change its {slot} core to {id} while the game runs";

    // ---- save states (the shared mechanism: the apps keep a quick slot per game and file / key-value storage; the NES has its own, older one and does not use this) ----

    /// <summary>True when <see cref="SaveState"/> and <see cref="LoadState"/> work for this session. A session that has no state says so here and in <see cref="StateUnsupportedReason"/>; it never pretends.</summary>
    bool SupportsState => false;

    /// <summary>Why this session has no save states, in words fit for a message to the user.</summary>
    string StateUnsupportedReason => $"The {Consoles.DisplayName(Console)} has no save states yet.";

    /// <summary>
    /// The whole machine as bytes (a <see cref="ConsoleStateFile"/>): taken between frames, never in the middle of one. It carries the game's id and the ids of the parts the state depends on (the sound unit), so
    /// <see cref="LoadState"/> can refuse a state that does not belong. Throws <see cref="NotSupportedException"/> when <see cref="SupportsState"/> is false.
    /// </summary>
    byte[] SaveState() => throw new NotSupportedException(StateUnsupportedReason);

    /// <summary>
    /// Puts a state made by <see cref="SaveState"/> back. Throws <see cref="InvalidDataException"/> (the message is for the user) when the state is for another game, console or sound unit, or is damaged, and leaves the
    /// machine as it was; <see cref="NotSupportedException"/> when <see cref="SupportsState"/> is false. A state is never loaded into a different sound unit and no part is swapped to make it fit.
    /// </summary>
    void LoadState(byte[] state) => throw new NotSupportedException(StateUnsupportedReason);
}
