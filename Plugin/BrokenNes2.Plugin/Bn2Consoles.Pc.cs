using BrokenNes2.Pc;

namespace BrokenNes2;

/// <summary>
/// The x86 PC console (console 5, 2026-11-10): the BrokenNes x86 console's sound devices as chips, Direct mode only (the plugin runs no DOS program: ROM mode is refused on this console).
/// One instance = one voice of the device (Pc/PcVoice.cs): an FM voice with its own patch on the AdLib engines, the speaker's square, a Tandy tone or noise, a DAC waveform on the Sound Blaster,
/// Covox and Gravis Ultrasound. Appended after every earlier chip in this fixed order (append-only: a saved project stores the chip's index). The chip id is "PC_" + the x86 APU id (PcSound).
/// </summary>
public static partial class Cx
{
    public const int Pc = 5;

    private static class PcTable
    {
        internal static readonly (string Id, string Note)[] Ids =
        [
            ("PC_ADLIB", "AdLib OPL2 (ymfm): one FM voice, pick the patch with Instrument"),
            ("PC_ADLIB_M036", "AdLib on MAME 0.36 fmopl (art, bugs kept)"),
            ("PC_ADLIB_D072", "AdLib on DOSBox 0.72 (art: 22 kHz, 1 ms write blocks)"),
            ("PC_ADLIB_D074C", "AdLib on DOSBox 0.74 compat / ADLIBEMU (art)"),
            ("PC_ADLIB_D074F", "AdLib on DOSBox 0.74 DBOPL (art)"),
            ("PC_ADLIB_NUK174", "OPL3 core Nuked-OPL3 1.7.4 in OPL2 mode (Sound Blaster 16 / OPL3 FM)"),
            ("PC_SPEAKER", "PC speaker: the 8254's square, no volume"),
            ("PC_SB_D072", "Sound Blaster Pro 2 direct DAC (DOSBox 0.72): an 8-bit waveform, pick it with Instrument"),
            ("PC_TANDY_D072", "Tandy SN76496 (DOSBox 0.72): tone or noise with Instrument; mutes 5 s after the last write (kept)"),
            ("PC_GUS_D074", "Gravis Ultrasound (DOSBox 0.74): a looped 8-bit waveform"),
            ("PC_COVOX", "Covox Speech Thing: 8-bit DAC waveform"),
        ];
    }

    private static void AddPc(List<Chip> list)
    {
        foreach (var (id, note) in PcTable.Ids) list.Add(new Chip(id, Pc, note, IsPc: true));
    }

    /// <summary>The x86 APU id (PcSound) of a PC chip id.</summary>
    public static string PcApu(string chipId) => chipId[3..];

    /// <summary>What the Instrument parameter picks on a PC chip: 6 the OPL patch bank, 7 the DAC waveforms, 8 the Tandy's modes, 0 nothing (the speaker).</summary>
    public static int PcPatchBank(string chipId) => !chipId.StartsWith("PC_", StringComparison.OrdinalIgnoreCase) ? -1 : PcVoice.KindOf(PcApu(chipId)) switch
    {
        PcKind.Adlib => 6, PcKind.Tandy => 8, PcKind.Speaker => 0, _ => 7,
    };

    public static int PcPatchCount(int bank) => bank switch { 6 => OplBank.Count, 7 => PcWaves.Count, 8 => TandyModes.Names.Length, _ => 0 };
    public static int PcPatchClamp(int bank, int i) => bank switch { 6 => OplBank.Clamp(i), 7 => PcWaves.Clamp(i), 8 => TandyModes.Clamp(i), _ => i };
    public static (string Name, string Note, string Description)[] PcPatchItems(int bank) => bank switch
    {
        6 => OplBank.Patches.Select(p => (p.Name, p.Note, p.Description)).ToArray(),
        7 => PcWaves.Names.Select(n => (n, "8-bit", "one cycle of 256 points, played by the program through the DAC")).ToArray(),
        8 => TandyModes.Names.Select(n => (n, "SN76496", n.StartsWith("Tone") ? "tone 1" : "the noise channel, pitched by tone 3 (mode 3)")).ToArray(),
        _ => [],
    };
}
