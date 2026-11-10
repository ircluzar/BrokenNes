// The project's name in FL's main window title (ProjectFlp.cs reads the title; this part is pure so FruityHost `flp-test` checks it). FL 2026's real title form is NOT verified.
namespace BrokenNes2.Music.Flp;

public static class FlpTitle
{
    /// <summary>The project's name in a title like "overworld.flp - FL Studio 2026" or "*overworld - FL Studio" (no ".flp"); null when the title does not look like FL's.</summary>
    public static string? NameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string t = title.Trim();
        int fl = t.IndexOf(" - FL Studio", StringComparison.OrdinalIgnoreCase);
        if (fl <= 0) return null;
        string name = t[..fl].Trim().Trim('*').Trim();
        if (name.EndsWith(".flp", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = Path.GetFileName(name);
        return name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? name : null;
    }

}
