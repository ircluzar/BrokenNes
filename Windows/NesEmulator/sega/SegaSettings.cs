using System;
using System.Collections.Generic;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>The Master System family board revisions: the VDP and the board differ in what is drawn at the edges and in a few register behaviours.</summary>
public enum MasterSystemModel { Auto, MarkIII, Sms1, Sms2, GameGear, Sg1000, Sc3000 }

public enum RegionChoice { Auto, Ntsc, Pal }
public enum GenesisModelChoice { Auto, Model1, Model2, Model3 }

/// <summary>
/// The user-facing machine settings of the Sega consoles: region and Genesis model, each "Auto" until the user picks one, and the rule that turns Auto (plus the
/// ROM's own header) into the value a board is built with. Per-game: a setting stored with a game's configuration overrides these process-wide defaults, which exist so
/// headless runs and tests can choose without a user interface (<c>BROKENNES_SEGA_REGION=pal</c>, <c>BROKENNES_MD_MODEL=1</c>).
/// </summary>
public static class SegaSettings
{
    public static volatile RegionChoice Region = FromEnvironment("BROKENNES_SEGA_REGION", RegionChoice.Auto);
    public static volatile GenesisModelChoice GenesisModelDefault = FromEnvironment("BROKENNES_MD_MODEL", GenesisModelChoice.Auto);

    /// <summary>Genesis cartridges whose behaviour depends on the board revision, by product code from the header. Empty until the Genesis track measures which games need
    /// which model; the lookup is here so the table is data, not code.</summary>
    public static readonly Dictionary<string, GenesisModel> GenesisModelByProductCode = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The model a game runs on when nothing is chosen: the later revisions behave the same for nearly everything, and YM3438 sound (Model 2/3) has no ladder artefact.</summary>
    public const GenesisModel GenesisModelFallback = GenesisModel.Model2;

    private static T FromEnvironment<T>(string name, T fallback) where T : struct, Enum
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return fallback;
        v = v.Trim();
        if (int.TryParse(v, out int digit) && typeof(T) == typeof(GenesisModelChoice)) v = "Model" + digit;
        return Enum.TryParse(v, ignoreCase: true, out T parsed) ? parsed : fallback;
    }

    /// <summary>Region for a game: the user's choice, else what the ROM says (Genesis: its header; Master System: its file name, see <see cref="MasterSystemRegionFromName"/>). A game that lists several regions takes NTSC.</summary>
    public static SegaRegion ResolveRegion(RegionChoice? perGame, ConsoleKind console, byte[] rom, string fileName = "")
    {
        var choice = perGame is { } c && c != RegionChoice.Auto ? c : Region;
        if (choice == RegionChoice.Ntsc) return SegaRegion.Ntsc;
        if (choice == RegionChoice.Pal) return SegaRegion.Pal;
        return console == ConsoleKind.Genesis ? GenesisHeaderRegion(rom) : MasterSystemRegionFromName(fileName);
    }

    public static GenesisModel ResolveGenesisModel(GenesisModelChoice? perGame, byte[] rom)
    {
        var choice = perGame is { } c && c != GenesisModelChoice.Auto ? c : GenesisModelDefault;
        switch (choice)
        {
            case GenesisModelChoice.Model1: return GenesisModel.Model1;
            case GenesisModelChoice.Model2: return GenesisModel.Model2;
            case GenesisModelChoice.Model3: return GenesisModel.Model3;
        }
        string? code = GenesisProductCode(rom);
        return code != null && GenesisModelByProductCode.TryGetValue(code, out var m) ? m : GenesisModelFallback;
    }

    /// <summary>The board a Master System family cartridge runs on: the Game Gear console is the Game Gear, an SG-1000 / SC-3000 file is that machine, anything else an SMS 2.</summary>
    public static MasterSystemModel ResolveMasterSystemModel(MasterSystemModel? perGame, ConsoleKind console, string fileName)
    {
        if (perGame is { } m && m != MasterSystemModel.Auto) return m;
        if (console == ConsoleKind.GameGear) return MasterSystemModel.GameGear;
        return System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".sg" => MasterSystemModel.Sg1000,
            ".sc" => MasterSystemModel.Sc3000,
            _ => MasterSystemModel.Sms2,
        };
    }

    // ---- header reading (Sega Genesis technical overview, Plutiedev "ROM header"; SMS Power "ROM header") ----

    /// <summary>Genesis region from the header's region field at $1F0-$1F2: letters J, U, E, or one hex digit of a J=1 / U=4 / E=8 bit mask. NTSC when J or U is listed or nothing is known.</summary>
    public static SegaRegion GenesisHeaderRegion(byte[] rom)
    {
        byte[] md = SegaRomFormat.NormalizeGenesis(rom);
        if (md.Length < 0x1F3) return SegaRegion.Ntsc;
        bool j = false, u = false, e = false, any = false;
        for (int i = 0; i < 3; i++)
        {
            char c = (char)md[0x1F0 + i];
            if (c == ' ' || c == 0) continue;
            any = true;
            switch (c)
            {
                case 'J': j = true; break;
                case 'U': u = true; break;
                case 'E': e = true; break;
                default:
                    if (Uri.IsHexDigit(c) && i == 0)
                    {
                        int mask = Convert.ToInt32(c.ToString(), 16);
                        j |= (mask & 1) != 0; u |= (mask & 4) != 0; e |= (mask & 8) != 0;
                    }
                    break;
            }
        }
        if (!any || j || u) return SegaRegion.Ntsc;
        return e ? SegaRegion.Pal : SegaRegion.Ntsc;
    }

    /// <summary>
    /// Master System / Game Gear region. The cartridge header cannot say: its region nibble (4 = SMS export, 6 / 7 = Game Gear export) covers the American market, which ran
    /// NTSC, and the European one, which ran PAL. So the answer is NTSC unless the file's name carries a European or Australian tag the way the common dump sets write it
    /// ("(Europe)", "(E)", "(Australia)", "(PAL)"), and no American, Japanese or world tag beside it. The user's choice overrides this either way.
    /// </summary>
    public static SegaRegion MasterSystemRegionFromName(string fileName)
    {
        string n = System.IO.Path.GetFileNameWithoutExtension(fileName);
        bool pal = PalTag.IsMatch(n);
        bool ntsc = NtscTag.IsMatch(n);
        return pal && !ntsc ? SegaRegion.Pal : SegaRegion.Ntsc;
    }

    private static readonly System.Text.RegularExpressions.Regex PalTag =
        new(@"[\(\[](?:[^\)\]]*,\s*)?(Europe|Australia|PAL|E)(?:\s*,[^\)\]]*)?[\)\]]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex NtscTag =
        new(@"[\(\[](?:[^\)\]]*,\s*)?(USA|Japan|World|Korea|Brazil|NTSC|U|J|W|JU|UE|JE)(?:\s*,[^\)\]]*)?[\)\]]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The 14-character product code at $180 ("GM 00001009-00" style), trimmed, or null.</summary>
    public static string? GenesisProductCode(byte[] rom)
    {
        byte[] md = SegaRomFormat.NormalizeGenesis(rom);
        if (md.Length < 0x18E) return null;
        string s = System.Text.Encoding.ASCII.GetString(md, 0x183, 11).Trim('\0', ' ');
        return s.Length == 0 ? null : s;
    }
}
