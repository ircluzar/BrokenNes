// "This FL project" (Test MIDI export window): which .flp is the project this plugin instance sits in, so the button can take the project's own LAST SAVED file.
//
// What FL tells a native plugin (evidence, 2026-11-09; docs/projects/flp-music-pipeline.md section 6): the plugin's SDK mirror (Plugin/BrokenNes.Fruity/FruitySdk.cs, the layout Bendy uses)
// has no call that returns the project's path, and the repo holds no FL SDK header to look further (none may be downloaded for this work). FL calls FPD_ProjLoaded after loading a
// project but gives no path with it; CI_TrackName was the only context info tried, and the user's real FL answered 0 for it (reference_fl_real_conventions). So the plugin does not
// rely on FL for the path:
//   1. a name hint from FL's main window title (the editor's root window): FL shows the project's name there. The title's exact form in FL 2026 is NOT verified (only the user's FL
//      can show it); FlpTitle.NameFromTitle accepts "name.flp - FL Studio ...", "name - FL Studio ...", a leading or trailing "*" (unsaved changes), and nothing else.
//   2. the path picked once for that name, remembered in the plugin's preferences (key "FlpPath:" + the name, lower case);
//   3. otherwise the file dialog, once, starting in the folder of the last pick; the pick is remembered for the name.
// The file is read when the button is pressed: it is the project as FL LAST SAVED it (unsaved changes in FL are not in it: the window says the file's save time).
using System.Runtime.InteropServices;

using BrokenNes2.Music.Flp;

namespace BrokenNes2;

internal static unsafe partial class ProjectFlp
{
    [LibraryImport("user32.dll")] private static partial nint GetAncestor(nint hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")] private static partial int GetWindowText(nint hwnd, char* buffer, int size);

    /// <summary>Tests only: the title to read instead of FL's window (null: the real window).</summary>
    public static string? TitleOverride;

    /// <summary>The title of the window the editor sits in (FL's main window), or "".</summary>
    public static string HostTitle(nint editor)
    {
        if (TitleOverride != null) return TitleOverride;
        if (editor == 0) return "";
        try
        {
            var root = GetAncestor(editor, 2 /* GA_ROOT */);
            if (root == 0) return "";
            char* buf = stackalloc char[512];
            int n = GetWindowText(root, buf, 512);
            return n > 0 ? new string(buf, 0, n) : "";
        }
        catch { return ""; }
    }

    public static string PrefKey(string name) => "FlpPath:" + name.ToLowerInvariant();

    /// <summary>The remembered .flp for a project name, when it still exists.</summary>
    public static string? Remembered(string? name)
    {
        if (name == null) return null;
        var p = Prefs.Get(PrefKey(name));
        return p is { Length: > 0 } && File.Exists(p) ? p : null;
    }

    public static void Remember(string? name, string path)
    {
        Prefs.Set(PrefKey(name ?? Path.GetFileNameWithoutExtension(path)), path);
        if (Path.GetDirectoryName(path) is { Length: > 0 } d) Prefs.Set("FlpFolder", d);
    }
}
