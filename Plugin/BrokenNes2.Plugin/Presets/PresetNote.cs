// Round 6 (presets): the learned note behaviour a preset can carry. The preset file reserves a block for it (NOTE: a u16 version, then the bytes; version 0 = empty). What the `runaway` work learns from a corrupted SNES game is a RECIPE per way the
// game's driver keyed an instrument (SpcRecipe: the key-on settings and the timed changes it made while the note sounded). A SNES preset keeps the recipes of ITS sample, so that played with no ROM it can sound the way the game played it, not only as the plain sample:
//
//   NOTE version 1  = the recipes of the preset's sample, in the saved form of the SPR1 section (SpcRecipe.Write / SpcRecipe.Read: versioned, every count and range checked)
//   NOTE version 2  = reserved for a Genesis game's recipes (GenRecipe), NOT written yet (see docs/projects/round6-presets.md: they are keyed to a channel's patch as it was at the key-on, not to an instrument of the picker)
//
// A version this code does not know is kept byte for byte (a rename or an export loses nothing) and not played; the Presets tab says "the game's note behaviour" is in it.
// Only the recipes that play on the preset alone are kept: the ones that key this sample (SRCN) and never switch the voice to another sample (a preset holds one sample; the others are not in it).
using BrokenNes2.Instruments;

namespace BrokenNes2.Presets;

public static class PresetNote
{
    public const int SnesRecipes = 1;

    /// <summary>Whether a recipe plays on a preset of sample <paramref name="slot"/> alone: it keys that sample and every sample change in its macro is to that sample.</summary>
    public static bool PlaysOn(SpcRecipe r, int slot)
    {
        if (r.Srcn != slot) return false;
        foreach (var e in r.Events) if (e.Kind == SpcEv.Srcn && e.Value != slot) return false;
        return true;
    }

    /// <summary>The recipes of <paramref name="all"/> that play on sample <paramref name="slot"/> alone, at most <see cref="SpcRecipe.MaxRecipes"/>.</summary>
    public static SpcRecipe[] ForSample(IEnumerable<SpcRecipe> all, int slot) => all.Where(r => !r.Silent && PlaysOn(r, slot)).Take(SpcRecipe.MaxRecipes).ToArray();

    /// <summary>Puts <paramref name="recipes"/> (those of sample <paramref name="slot"/>) in <paramref name="d"/>'s reserved block as version 1; nothing changes when there are none.</summary>
    public static void Attach(PresetData d, IEnumerable<SpcRecipe> recipes, int slot)
    {
        if (PresetMutants.On(PresetMutants.NoteRecipesDropped)) return;
        var list = ForSample(recipes, slot);
        if (list.Length == 0 || d.Console != ConsoleId.Snes) return;
        d.NoteVersion = SnesRecipes;
        d.NoteBehaviour = SpcRecipe.Write(list);
    }

    /// <summary>The recipes a SNES preset carries (version 1), or null when it carries none this code plays (no block, another version, or bytes that are not a recipe list).</summary>
    public static SpcRecipe[]? SnesList(PresetData d) =>
        d.NoteVersion == SnesRecipes && d.Console == ConsoleId.Snes ? SpcRecipe.Read(d.NoteBehaviour) is { Length: > 0 } list ? list : null : null;

    /// <summary>Null when the block is something this version understands completely or does not know at all; the reason when it is version 1 and wrong.</summary>
    public static string? Check(PresetData d)
    {
        if (d.NoteVersion == SnesRecipes)
        {
            if (d.Console != ConsoleId.Snes) return "damaged (SNES note behaviour in a preset of another console)";
            var list = SpcRecipe.Read(d.NoteBehaviour);
            if (list == null || list.Length == 0) return "damaged (the game's note behaviour is not a recipe list)";
            if (list.Any(r => !PlaysOn(r, d.Index) && !PresetMutants.On(PresetMutants.NoteNotChecked))) return "damaged (the note behaviour is for another sample)";
        }
        return null;
    }

    public static string Describe(PresetData d) => d.NoteVersion == 0 ? "" : SnesList(d) is { } l ? $"{l.Length} game voice{(l.Length == 1 ? "" : "s")}" : "note behaviour this version cannot play";
}
