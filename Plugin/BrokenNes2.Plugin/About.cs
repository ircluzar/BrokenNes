using System.Reflection;
using System.Text;

namespace BrokenNes2;

/// <summary>The About window's text, and the plugin's one piece of memory: whether this machine has seen it. The window opens by itself
/// the first time the plugin opens (and again if <see cref="Revision"/> is raised because the text changed in a way worth showing again).</summary>
internal static class About
{
    /// <summary>Raise this when the About text changes so much that everyone should read it again.</summary>
    public const int Revision = 3;

    // ---- the shoutout (edit the words here) ----
    public const string ShoutLabel = "WHERE TO GET THE REAL DEAL";
    public const string ShoutHeading = "Check out VSTs from Plogue";
    public const string ShoutLine = "Real, human-crafted plugins that are correctly accurate.";
    public const string ShoutBody = "If you make real work, buy their plugins. They put an incredible amount of effort into them.";
    public const string ShoutNote = "(This is a personal endorsement of Plogue, not affiliated, I just think their products are bangers)";
    public const string ShoutUrl = "https://www.plogue.com/";
    public const string Footer = "Credits, licences and what the plugin stores: see THIRD_PARTY_NOTICES and LICENSE.txt in the BrokenNes repository.";

    private static readonly object gate = new();
    private static bool shownThisSession;

    public static string Version => typeof(About).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(About).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>The text (Resources/about.txt, embedded): "# " starts a heading, "- " a bullet, a blank line ends a paragraph.</summary>
    public static string Text()
    {
        try
        {
            using var s = typeof(About).Assembly.GetManifestResourceStream("about.txt");
            if (s != null)
            {
                using var r = new StreamReader(s, Encoding.UTF8);
                return r.ReadToEnd().Replace("{version}", Version);
            }
        }
        catch { }
        return "# About\nThe About text is missing from this build.";
    }

    /// <summary>True the first time it is asked on a machine that has not seen this revision, and from then on false: the caller opens the
    /// window. Several instances opening their editors in one FL session ask only once. If the memory cannot be written the window shows
    /// again next time rather than never.</summary>
    public static bool ClaimFirstShowing()
    {
        lock (gate)
        {
            if (shownThisSession) return false;
            shownThisSession = true;
            if (Prefs.GetInt(SeenName, 0) >= Revision) return false;
            Prefs.SetInt(SeenName, Revision);
            return true;
        }
    }

    /// <summary>Test hook: forget this session's claim, and (for the first run on a machine) what was remembered.</summary>
    public static void ResetForTests(bool deleteState)
    {
        lock (gate) shownThisSession = false;
        if (deleteState) Prefs.Delete(SeenName);
    }

    private const string SeenName = "AboutRevisionSeen";
}
