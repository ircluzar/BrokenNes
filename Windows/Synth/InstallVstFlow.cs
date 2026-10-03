using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace BrokenNes.Windows.Synth
{
    /// <summary>
    /// "Install to FL Studio": ask which FL Studio, install (with the UAC prompt), say what happened. The same flow is behind the
    /// Config > Synthesizer Mode menu item and <c>BrokenNes.Windows.exe --install-vst</c>, which runs it without starting the emulator.
    /// </summary>
    /// <remarks>
    /// <c>--install-vst [--fl &lt;FL Studio folder&gt;]... [--quiet] [--result &lt;file&gt;] [--dll &lt;plugin&gt;]</c>. With --fl the picker is skipped
    /// (that is how it is tested and scripted); --quiet shows no windows; --result writes one line per target. <c>--elevated</c> is the
    /// program's own second half, started with administrator rights: it never asks for them again.
    /// Exit code: 0 everything installed or already current, 1 something failed, 2 nothing happened because the user backed out.
    /// </remarks>
    internal static class InstallVstFlow
    {
        public const int ExitOk = 0, ExitFailed = 1, ExitCancelled = 2;
        private const string Title = "Install BrokenNes 2 to FL Studio";

        /// <summary>The <c>--install-vst</c> entry point: no emulator, no main window.</summary>
        public static int RunCommand(string[] args)
        {
            bool elevated = false, quiet = false, list = false;
            string? resultFile = null, dllArg = null;
            var folders = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--elevated": elevated = true; break;
                    case "--quiet": quiet = true; break;
                    case "--list": list = true; break;
                    case "--result" when i + 1 < args.Length: resultFile = args[++i]; break;
                    case "--dll" when i + 1 < args.Length: dllArg = args[++i]; break;
                    case "--fl" when i + 1 < args.Length: folders.Add(args[++i]); break;
                }
            }
            if (!quiet) { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); }

            string? dll = dllArg ?? PluginInstaller.FindPluginDll();

            if (list)
            {
                // what the picker would offer, one line each (edition, name, certified, what it holds now, root); for tests and curiosity
                var lines = FlStudioLocator.Detect().Select(f => $"{f.Edition}\t{f.Name}\t{f.Certified}\t{PluginInstaller.StatusOf(f, dll)}\t{f.Root}").ToList();
                if (resultFile != null) File.WriteAllLines(resultFile, lines);
                else if (!quiet) MessageBox.Show(string.Join(Environment.NewLine, lines), Title);
                return ExitOk;
            }
            if (dll == null || !File.Exists(dll))
            {
                string why = dllArg != null ? $"the plugin file does not exist: {dllArg}" : MissingDllText();
                if (resultFile != null) File.WriteAllText(resultFile, "Failed\t\t" + why + Environment.NewLine);
                if (!quiet) MessageBox.Show(why, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return ExitFailed;
            }

            if (folders.Count == 0)
            {
                if (quiet)
                {
                    if (resultFile != null) File.WriteAllText(resultFile, "Failed\t\t--quiet needs at least one --fl <FL Studio folder>" + Environment.NewLine);
                    return ExitFailed;
                }
                return RunInteractive(null);
            }

            var results = new List<InstallResult>();
            var targets = new List<FlStudioInstall>();
            foreach (var f in folders)
            {
                var t = FlStudioLocator.FromFolder(f);
                if (t != null) targets.Add(t);
                else results.Add(new(new FlStudioInstall(Path.GetFileName(f.TrimEnd('\\', '/')), f, 0), InstallOutcome.Failed,
                    "not an FL Studio folder (no FL64.exe, no Plugins\\Fruity folder)"));
            }
            results.AddRange(PluginInstaller.InstallAll(targets, dll, allowElevation: !elevated));

            if (resultFile != null) PluginInstaller.WriteResultFile(resultFile, results);
            if (!quiet && !elevated) ShowSummary(null, results);
            return ExitCodeOf(results);
        }

        /// <summary>The picker, the install, the summary. <paramref name="owner"/> is the window it opens over (null from the command line).</summary>
        public static int RunInteractive(IWin32Window? owner)
        {
            string? dll = PluginInstaller.FindPluginDll();
            if (dll == null)
            {
                MessageBox.Show(owner, MissingDllText(), Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return ExitFailed;
            }

            IReadOnlyList<FlStudioInstall> pending;
            using (var dialog = new InstallVstDialog(dll, FlStudioLocator.Detect()))
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return ExitCancelled;
                pending = dialog.Selected;
            }

            // FL running from a target is the one failure the user can fix on the spot: let them close it and try again
            var latest = new Dictionary<string, InstallResult>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                foreach (var r in PluginInstaller.InstallAll(pending, dll, allowElevation: true)) latest[r.Target.Root] = r;
                var running = latest.Values.Where(r => r.Outcome == InstallOutcome.FlRunning).ToList();
                if (running.Count == 0) break;
                var answer = MessageBox.Show(owner, Summarize(latest.Values) + "\n\nClose FL Studio, then press Retry.", Title,
                    MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                if (answer != DialogResult.Retry) break;
                pending = running.Select(r => r.Target).ToList();
            }
            var all = latest.Values.ToList();
            ShowSummary(owner, all);
            return ExitCodeOf(all);
        }

        private static int ExitCodeOf(IReadOnlyCollection<InstallResult> results) =>
            results.All(r => r.Ok) ? ExitOk
            : results.All(r => r.Outcome == InstallOutcome.Cancelled) ? ExitCancelled
            : ExitFailed;

        private static string MissingDllText() =>
            "The FL Studio plugin (Plugin\\" + PluginInstaller.PluginFile + ") is not next to the program, so there is nothing to install.\n\n" +
            "This build of BrokenNes was made without it. The plugin is built separately (Plugin\\build-dist.ps1) and the desktop build copies it in.";

        /// <summary>One line per target, outcome first.</summary>
        private static string Summarize(IEnumerable<InstallResult> results)
        {
            var sb = new StringBuilder();
            foreach (var r in results)
            {
                string head = r.Outcome switch
                {
                    InstallOutcome.Installed => "Installed",
                    InstallOutcome.AlreadyCurrent => "Already up to date",
                    InstallOutcome.FlRunning => "FL Studio is running",
                    InstallOutcome.Cancelled => "Cancelled",
                    InstallOutcome.NeedsAdmin => "Needs administrator rights",
                    _ => "Failed",
                };
                sb.Append(head).Append(": ").Append(r.Target.Name);
                if (!r.Ok) sb.Append("\n    ").Append(r.Message);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private static void ShowSummary(IWin32Window? owner, IReadOnlyList<InstallResult> results)
        {
            string text = Summarize(results);
            bool installed = results.Any(r => r.Ok);
            if (installed)
                text += "\n\nStart FL Studio (restart it if it was open) and look for \"Bogue :: BrokenNes 2\" in the plugin picker.";
            MessageBox.Show(owner, text, Title, MessageBoxButtons.OK, results.All(r => r.Ok) ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
