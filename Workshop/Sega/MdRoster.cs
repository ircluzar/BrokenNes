using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The twenty Genesis roster games (<c>docs/projects/sega-rosters.md</c>), by number, and where the library keeps them. File names only: the ROMs are never in git. <c>BROKENNES_MD_ROMS</c> overrides the folder.
/// </summary>
internal static class MdRoster
{
    public static string Dir => Environment.GetEnvironmentVariable("BROKENNES_MD_ROMS") is { Length: > 0 } e ? e : @"X:\EMULATION\Sega Genesis";

    public sealed record Game(int Number, string Short, string File)
    {
        public string Path => System.IO.Path.Combine(Dir, File);
        public bool Exists => System.IO.File.Exists(Path);
        public byte[] Load() => System.IO.File.ReadAllBytes(Path);
    }

    public static readonly Game[] Games =
    {
        new(1, "Flicky", "Flicky (JUE) [!].bin"),
        new(2, "Altered Beast", "Altered Beast (JU) (REV 01) [!].bin"),
        new(3, "Sonic the Hedgehog", "Sonic the Hedgehog (JUE) [!].bin"),
        new(4, "Golden Axe", "Golden Axe (JU) (REV 00) [!].bin"),
        new(5, "Strider", "Strider (UE) [!].bin"),
        new(6, "Streets of Rage", "Streets of Rage (JUE) (REV 00) [!].bin"),
        new(7, "Castle of Illusion", "Mickey Mouse - Castle of Illusion (U) [!].bin"),
        new(8, "Ecco the Dolphin", "ECCO (U) [!].bin"),
        new(9, "Sonic the Hedgehog 2", "Sonic the Hedgehog 2 (JUE) [!].bin"),
        new(10, "Kid Chameleon", "Kid Chameleon (JUE) [!].bin"),
        new(11, "Gunstar Heroes", "Gunstar Heroes (E) [!].bin"),
        new(12, "Streets of Rage 2", "Streets of Rage 2 (U) [!].bin"),
        new(13, "Shinobi III", "Shinobi 3 - Return of the Ninja Master (U) [!].bin"),
        new(14, "Castlevania: Bloodlines", "Castlevania - Bloodlines (U) [!].bin"),
        new(15, "Comix Zone", "Comix Zone (4) [!].bin"),
        new(16, "Contra: Hard Corps", "Contra - Hard Corps (U) [!].bin"),
        new(17, "Phantasy Star IV", "Phantasy Star IV (4) [!].bin"),
        new(18, "Sonic the Hedgehog 3", "Sonic The Hedgehog 3 (U) [!].gen"),
        new(19, "Streets of Rage 3", "Streets of Rage 3 (E).bin"),
        new(20, "Sonic & Knuckles + Sonic 3", "Sonic and Knuckles & Sonic 3 (JUE) [!].bin"),
    };

    public static Game Get(int number) => Games.First(g => g.Number == number);

    /// <summary>"3", "3,5,7", "1-5" or "all": the roster games named.</summary>
    public static IEnumerable<Game> Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Equals("all", StringComparison.OrdinalIgnoreCase)) return Games;
        var list = new List<Game>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-');
            if (dash > 0) { int a = int.Parse(part[..dash]), b = int.Parse(part[(dash + 1)..]); list.AddRange(Games.Where(g => g.Number >= a && g.Number <= b)); }
            else list.Add(Get(int.Parse(part)));
        }
        return list;
    }
}
