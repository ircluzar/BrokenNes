// "This FL project" for the games that are not VRUN NES (Music/Flp/GameTargets.cs; docs/projects/flp-music-pipeline.md section 8): the game's own toolchain builds a test image with the
// song (a BrokenDev script, run as a process on a worker thread), the fence report is read back and shown, and the image is loaded into this emulator in ROM mode when the plugin has its
// console (SNES, WideMess, Genesis). DOS / Windows 95 builds and Nano GB files are made and reported, not played here (the window says where they are). Never silent: a missing
// toolchain, a failed build or a missing file is the result line with the reason.
using System.Diagnostics;
using BrokenNes2.Music.Flp;

namespace BrokenNes2;

public sealed partial class Emulator
{
    /// <summary>Which game "This FL project" builds for (remembered in the preferences as FlpTarget).</summary>
    internal GameTarget FlpTarget = GameTargets.ById(Prefs.Get("FlpTarget"));

    internal void NextFlpTarget() { FlpTarget = GameTargets.Next(FlpTarget); Prefs.Set("FlpTarget", FlpTarget.Id); }

    /// <summary>Test hook: the command is not run, its line is the result (so a check can see what would run without a toolchain).</summary>
    internal static bool GameBuildDryRun;

    internal string? BuildInGame(string flp)
    {
        var t = FlpTarget;
        if (SongUi.Busy) return "busy: a song is being converted";
        string? repo = GameTargets.RepoFor(t, Prefs.Get("RepoPath"));
        if (repo == null) return $"{t.Label}: the build script {t.Script} was not found (set BROKENNES_REPO to the BrokenNes folder): nothing changed";
        string outDir = Path.Combine(Path.GetTempPath(), "BrokenNes2", "FlpBuilds");
        var (program, args) = GameTargets.Command(t, repo, flp, outDir);
        if (GameBuildDryRun) { SongUi.SetResult($"would run: {program} {string.Join(" ", args)}", true); return null; }
        SongUi.Busy = true;
        SongUi.Status = $"building {Path.GetFileName(flp)} for {t.Label} with the game's own toolchain ...";
        var th = new Thread(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process p;
                try { p = Process.Start(psi)!; }
                catch (Exception e) { SongUi.SetResult($"{t.Label}: {program} could not be started ({e.Message.TrimEnd('.')}): nothing changed", false); return; }
                p.StandardInput.Close();
                var o = p.StandardOutput.ReadToEndAsync(); var er = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15 * 60 * 1000)) { try { p.Kill(true); } catch { } SongUi.SetResult($"{t.Label}: the build took more than 15 minutes and was stopped", false); return; }
                string log = o.Result + er.Result;
                BrokenNes.Fruity.Diag.Log($"This FL project -> {t.Label}: {program} {string.Join(" ", args)} exit {p.ExitCode}\n{log}");
                string fenceFile = GameTargets.FenceFile(t, flp, outDir);
                string fence = File.Exists(fenceFile) ? FenceHeadline(fenceFile) : "no fence report";
                if (p.ExitCode != 0)
                {
                    string why = log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("FAIL")) ?? $"exit {p.ExitCode}";
                    SongUi.SetResult($"{t.Label}: the build failed ({why}); {fence}. Log: the diagnostics log", false);
                    return;
                }
                string? product = GameTargets.Product(t, flp, outDir);
                if (product == null || !File.Exists(product)) { SongUi.SetResult($"{t.Label}: the build said OK but made no {product}; {fence}", false); return; }
                if (!t.PlaysHere) { SongUi.SetResult($"{t.Label}: built {product} ({fence}). It does not play in the plugin: {(t.Id == "nano-gb" ? "open it with the Nano GB web library" : "run it on BrokenNes's x86 console or DOSBox")}", true); return; }
                if (LoadRomFile(product) is { } refused) { SongUi.SetResult($"{t.Label}: built {product}, but it did not load: {refused}", false); return; }
                SongUi.SetResult($"{t.Label}: plays the title screen's song (the game's own driver); {fence}", true);
                SongUi.SetNote($"full fence report: {fenceFile}");
            }
            catch (Exception e) { SongUi.SetResult($"{t.Label}: {e.Message}", false); }
            finally { SongUi.Status = ""; SongUi.Busy = false; }
        }) { IsBackground = true, Name = "BrokenNes2 game build" };
        th.Start();
        return null;
    }

    /// <summary>"N over the limit, M played differently" and the first OVER line, from the fence report the script wrote (.fence.txt, or flp2nano's .fence.json).</summary>
    internal static string FenceHeadline(string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                int over = CountOf(text, "\"level\":\"over\""), note = CountOf(text, "\"level\":\"note\"");
                return $"fence: {over} over the limit, {note} played differently";
            }
            var lines = text.Split('\n').Select(l => l.Trim()).ToList();
            string head = lines.FirstOrDefault(l => l.Contains(" over the limit")) ?? "";
            string first = lines.FirstOrDefault(l => l.StartsWith("OVER")) ?? "";
            return "fence " + (head.Contains(':') ? head[(head.IndexOf(':') + 1)..].Trim() : head) + (first.Length > 0 ? ": " + first : "");
        }
        catch (Exception e) { return "fence report unreadable: " + e.Message; }
    }

    private static int CountOf(string s, string what) { int n = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; } return n; }
}
