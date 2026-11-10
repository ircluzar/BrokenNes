// The music pipeline's targets for "This FL project" (docs/projects/flp-music-pipeline.md section 8): which game engine the FL project is heard in. VRUN NES is built inside the plugin (the
// embedded sound-test template); the others are built by their game's own toolchain through a BrokenDev script, outside the plugin (process), and the result is loaded into the plugin's
// emulator in ROM mode when the plugin has that console (SNES, WideMess, Genesis). DOS / Windows 95 builds and Nano GB are made and reported, not played here (the plugin has no PC console
// and Nano GB is a web library): the window says where the file is. Pure: no UI, no processes.
namespace BrokenNes2.Music.Flp;

/// <summary>Inside: built in the plugin from an embedded template with the game's own driver (Vaults GB / GG, CTW SMS: Emulator.FlpGame.cs), no script or toolchain.</summary>
public sealed record GameTarget(string Id, string Label, string Engine, bool PlaysHere, string Script, bool Inside = false);

public static class GameTargets
{
    public static readonly GameTarget[] All =
    [
        new("vrun-nes", "VRUN NES", "", true, ""),
        new("supervrun-snes", "Super VRUN SNES", "snes", true, @"BrokenDev\tools\music\flp\build-supervrun.ps1"),
        new("supervrun-wms", "Super VRUN WideMess", "wms", true, @"BrokenDev\tools\music\flp\build-supervrun.ps1"),
        new("supervrun-md", "Super VRUN MD", "md", true, @"BrokenDev\tools\music\flp\build-supervrun.ps1"),
        new("supervrun-dos", "Super VRUN DOS", "dos", false, @"BrokenDev\tools\music\flp\build-supervrun.ps1"),
        new("supervrun-95", "Super VRUN 95", "95", false, @"BrokenDev\tools\music\flp\build-supervrun.ps1"),
        new("vaults-gb", "Vaults GB", "", true, "", Inside: true),
        new("vaults-gg", "Vaults GG", "", true, "", Inside: true),
        new("ctw-sms", "CTW SMS", "", true, "", Inside: true),
        new("nano-gb", "Nano GB (web)", "", false, @"Nano\gb\tools\flp2nano\flp2nano.mjs"),
    ];

    public static GameTarget Next(GameTarget t) => All[(Array.IndexOf(All, t) + 1) % All.Length];
    public static GameTarget ById(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    /// <summary>The BrokenNes repository the scripts live in: BROKENNES_REPO, else the folder remembered, else the usual place on this machine; null when none has the script.</summary>
    public static string? RepoFor(GameTarget t, string? remembered)
    {
        foreach (var c in new[] { Environment.GetEnvironmentVariable("BROKENNES_REPO"), remembered, @"C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes" })
            if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, t.Script))) return c;
        return null;
    }

    /// <summary>The command line for a target (program, arguments). The build script writes &lt;out&gt;\&lt;name&gt;.&lt;engine&gt;.&lt;ext&gt; and .fence.txt; flp2nano writes &lt;out&gt;\&lt;name&gt;.nano.*.</summary>
    public static (string Program, List<string> Args) Command(GameTarget t, string repo, string flp, string outDir)
    {
        string script = Path.Combine(repo, t.Script);
        if (t.Id == "nano-gb") return ("node", [script, flp, "--out", outDir]);
        return ("pwsh", ["-NoProfile", "-File", script, "-Flp", flp, "-Engine", t.Engine, "-Out", outDir]);
    }

    /// <summary>The file the build made for a target (null for Nano GB: a .mid + sidecar for the web library).</summary>
    public static string? Product(GameTarget t, string flp, string outDir)
    {
        string safe = string.Concat(Path.GetFileNameWithoutExtension(flp).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        string? ext = t.Engine switch { "snes" => "sfc", "wms" => "wms", "md" => "bin", "dos" or "95" => "exe", _ => null };
        return ext == null ? Path.Combine(outDir, Path.GetFileNameWithoutExtension(flp) + ".nano.mid") : Path.Combine(outDir, $"{safe}.{t.Engine}.{ext}");
    }

    public static string FenceFile(GameTarget t, string flp, string outDir)
    {
        string safe = string.Concat(Path.GetFileNameWithoutExtension(flp).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        return t.Id == "nano-gb" ? Path.Combine(outDir, Path.GetFileNameWithoutExtension(flp) + ".nano.fence.json") : Path.Combine(outDir, $"{safe}.{t.Engine}.fence.txt");
    }
}
