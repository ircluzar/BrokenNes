using System;

namespace NesEmulator.Sega;

/// <summary>
/// The machine settings the apps let the user choose for the Sega consoles, handed to <c>ConsoleSessions.Create</c>: the video standard (NTSC 60 Hz or PAL 50 Hz), the Genesis board revision (Model 1, 2 or 3) and the
/// Master System board (Mark III, SMS 1, SMS 2). <c>Auto</c> for each means what it always meant: the region from the file name's tag (Master System) or the header (Genesis), Model 1, and the board the game needs. The Game Gear
/// is a 60 Hz machine whatever it is told unless the region is chosen explicitly. A change needs a new session (the boards' states include model and region), so the apps restart the game on it.
/// </summary>
public sealed record SegaOptions(RegionChoice Region = RegionChoice.Auto, GenesisModelChoice GenesisModel = GenesisModelChoice.Auto, MasterSystemModel MasterSystemModel = MasterSystemModel.Auto)
{
    public static readonly SegaOptions Default = new();

    // ---- the keys the config file, the HTTP API and the Lite page store (stable; the enums' numbers are not) ----
    public static string Key(RegionChoice r) => r switch { RegionChoice.Ntsc => "ntsc", RegionChoice.Pal => "pal", _ => "auto" };
    public static RegionChoice RegionFromKey(string? key) => (key ?? "").Trim().ToLowerInvariant() switch { "ntsc" or "60" or "60hz" => RegionChoice.Ntsc, "pal" or "50" or "50hz" => RegionChoice.Pal, _ => RegionChoice.Auto };

    public static string Key(GenesisModelChoice m) => m switch { GenesisModelChoice.Model1 => "1", GenesisModelChoice.Model2 => "2", GenesisModelChoice.Model3 => "3", _ => "auto" };
    public static GenesisModelChoice GenesisModelFromKey(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "1" or "model1" or "m1" => GenesisModelChoice.Model1, "2" or "model2" or "m2" => GenesisModelChoice.Model2, "3" or "model3" or "m3" => GenesisModelChoice.Model3, _ => GenesisModelChoice.Auto,
    };

    public static string Key(MasterSystemModel m) => m switch { MasterSystemModel.MarkIII => "markiii", MasterSystemModel.Sms1 => "sms1", MasterSystemModel.Sms2 => "sms2", _ => "auto" };
    public static MasterSystemModel MasterSystemModelFromKey(string? key) => (key ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "") switch
    {
        "markiii" or "mark3" => MasterSystemModel.MarkIII, "sms1" => MasterSystemModel.Sms1, "sms2" => MasterSystemModel.Sms2, _ => MasterSystemModel.Auto,
    };

    // ---- the words for menus (one place, so the desktop, the HTTP API and Lite say the same) ----
    public static readonly (string Key, string Label)[] RegionChoices =
    {
        ("auto", "Automatic (from the game: a (Europe) tag or the header says PAL)"), ("ntsc", "NTSC (60 Hz)"), ("pal", "PAL (50 Hz)"),
    };
    public static readonly (string Key, string Label)[] GenesisModelChoices =
    {
        ("auto", "Automatic (Model 1 unless a game needs another)"), ("1", "Model 1 (the original: discrete DAC, ladder effect)"), ("2", "Model 2 (YM3438, no ladder effect)"), ("3", "Model 3"),
    };
    public static readonly (string Key, string Label)[] MasterSystemModelChoices =
    {
        ("auto", "Automatic (an SMS 2 unless the game needs the original VDP)"), ("markiii", "Mark III"), ("sms1", "Master System 1 (original VDP)"), ("sms2", "Master System 2"),
    };
}
