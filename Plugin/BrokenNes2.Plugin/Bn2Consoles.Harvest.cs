using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>
/// The harvested sound engines (2026-11-10, "ported cores in brokennes vst"): every engine the Foreign APU and APU harvest climbs ported into BrokenNes, offered as plugin chips under their console.
/// Appended after every earlier chip in the fixed order of <see cref="HarvestIds"/> (a saved project stores the chip's index: never reorder, never insert, only append at the end).
/// Direct mode drives each one exactly like its console's stock chip: the NES-board APU id is the chip id (a NES foreign chip such as POKY, or a bridge with the engine fixed such as DMG_GNUB, the stock DMG bridge
/// heard through gnuboy), so FL's notes, Pulse duty and the Instrument patch bank (MD_* = the Genesis FM patch, SMSFM_* = the YM2413 instrument) are the stock chip's. ROM mode runs the console's game on the same engine
/// (the bare engine id: GNUB on the Game Boy, ZS15 on the SNES ...). These are art instruments: ports of old emulators with their bugs kept, not accurate chips.
/// </summary>
public static partial class Cx
{
    /// <summary>Append-only. (id, console, short name of the engine). In a nested class: <see cref="Chips"/> is built by a static initializer in another file of this partial class, whose order against this file's is not defined.</summary>
    private static (string Id, int Console, string Name)[] HarvestIds => HarvestTable.Ids;

    private static class HarvestTable
    {
    internal static readonly (string Id, int Console, string Name)[] Ids =
    [
        // NES-board foreign engines (APU family "Foreign"): old NES emulators' 2A03s and other machines' chips driven by the NES registers
        ("DARC", Nes, "DarcNES 2A03"), ("FCEU", Nes, "FCE Ultra 2A03"), ("INES", Nes, "iNES 2A03"), ("NSTC", Nes, "NESticle 2A03"), ("TUXN", Nes, "TuxNES 2A03"), ("VNES", Nes, "VirtuaNES 0.97 2A03"),
        ("PNES", Nes, "PocketNES 2A03"), ("NSDS", Nes, "nesDS 2.0.1 2A03"), ("NOFR", Nes, "nofrendo 2A03 (retro-go)"),
        ("PCEG", Nes, "foreign chip: PC Engine PSG (PCE-GO)"), ("POKY", Nes, "foreign chip: Atari POKEY (atari800)"), ("AY124", Nes, "foreign chip: AY-3-8910 (MAME 0.124)"),
        ("RSID16", Nes, "foreign chip: C64 SID (reSID 0.16)"), ("FSID", Nes, "foreign chip: C64 SID (VICE FastSID)"),
        // Game Boy
        ("DMG_GNUB", GameBoy, "gnuboy 1.0.3"), ("DMG_VBA", GameBoy, "VisualBoyAdvance 1.7.2"), ("DMG_VBAM", GameBoy, "VBA-M 2007"), ("DMG_TGBD", GameBoy, "TGB Dual"), ("DMG_GYOB", GameBoy, "GameYob DS"), ("DMG_MGB", GameBoy, "minigb_apu 2022"),
        // SNES
        ("SNES_ZS15", Snes, "ZSNES 0.15"), ("SNES_ZS151", Snes, "ZSNES 1.51"), ("SNES_S916", Snes, "Snes9x 1.16a"), ("SNES_S9X1", Snes, "Snes9x 1.43"), ("SNES_S951", Snes, "Snes9x 1.51"),
        ("SNES_OSPC", Snes, "libOpenSPC"), ("SNES_SNDS", Snes, "SNEmulDS"), ("SNES_PSPC3", Snes, "PocketSPC 0.3"),
        // Master System PSG, then its FM chip
        ("SMS_BLST", MasterSystem, "BlastEm 0.3.1 PSG"), ("SMS_M036", MasterSystem, "MAME 0.36 PSG"), ("SMS_GPGX", MasterSystem, "Genesis Plus GX 1.3 PSG"), ("SMS_GWEN", MasterSystem, "Gwenesis PSG"),
        ("SMS_GENS", MasterSystem, "Gens 2.10 PSG"), ("SMS_PICO", MasterSystem, "PicoDrive 1.85 PSG"), ("SMS_DARC", MasterSystem, "DarcNES PSG"), ("SMS_DEGA", MasterSystem, "Dega 1.16 PSG"), ("SMS_SMSA", MasterSystem, "SMSAdvance 2.5 PSG"),
        ("SMSFM_M036FM", MasterSystem, "MAME 0.36 YM2413"), ("SMSFM_E055", MasterSystem, "emu2413 0.55 YM2413"),
        // Genesis
        ("MD_GENS", Genesis, "Gens 2.10"), ("MD_PICO", Genesis, "PicoDrive 1.85"), ("MD_M036", Genesis, "MAME 0.36"), ("MD_BLST", Genesis, "BlastEm 0.3.1"), ("MD_GPGX", Genesis, "Genesis Plus GX 1.3"), ("MD_GWEN", Genesis, "Gwenesis (retro-go)"),
        // appended 2026-11-12 (APU harvest round 2)
        ("NSTRJ", Nes, "NesterJ 1.11 2A03 (PSP)"), ("NSTRDC", Nes, "NesterDC 7.1.0 2A03 (Dreamcast)"),
    ];
    }

    private static void AddHarvest(List<Chip> list)
    {
        foreach (var h in HarvestIds)
        {
            string? found = CoreRegistry.ApuIds.FirstOrDefault(i => i.Equals(h.Id, StringComparison.OrdinalIgnoreCase));
            // a missing core would move every later index: refuse loudly rather than shift saved projects
            if (found == null) throw new InvalidOperationException($"harvest chip {h.Id} is not a registered APU core: the plugin's chip list would move");
            list.Add(new Chip(found, h.Console, $"ported: {h.Name} (art, bugs kept)"));
        }
    }

    /// <summary>The stock chip a harvested bridge stands for (DMG_GNUB -> DMG, SMSFM_E055 -> SMSFM); any other id as it is.</summary>
    public static string HarvestBase(string chipId) => CoreCatalog.PortedBridgeEngine(chipId) != null ? chipId[..chipId.IndexOf('_')] : chipId;

    /// <summary>The console sound unit a harvested bridge's game runs on in ROM mode (the bare engine id: GNUB, ZS15, M036FM ...), or null for any other chip (a NES chip runs as it is).</summary>
    public static string? HarvestEngine(string chipId) => CoreCatalog.PortedBridgeEngine(chipId)?.Engine;
}
