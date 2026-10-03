using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;

namespace BrokenNes.Windows.Synth
{
    internal enum InstallOutcome { Installed, AlreadyCurrent, FlRunning, NeedsAdmin, Cancelled, Failed }

    internal sealed record InstallResult(FlStudioInstall Target, InstallOutcome Outcome, string Message)
    {
        public bool Ok => Outcome is InstallOutcome.Installed or InstallOutcome.AlreadyCurrent;
    }

    /// <summary>
    /// Puts BrokenNes2_x64.dll into FL Studio's native generator folder. That folder is under Program Files, so the copy usually needs
    /// administrator rights: the install is tried as the current user first, and whatever was refused is retried by an elevated copy of
    /// this program (one UAC prompt for all of it). Nothing else on the machine is touched.
    /// </summary>
    internal static class PluginInstaller
    {
        /// <summary>FL finds a native plugin by its file name: <c>Generators\BrokenNes2\BrokenNes2_x64.dll</c>. (FL shows it as "Bogue :: BrokenNes 2"; "::" is not valid in a file name.)</summary>
        public const string PluginFolder = "BrokenNes2", PluginFile = "BrokenNes2_x64.dll";

        public static bool IsAdmin()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>The plugin build to install: the one shipped beside the program (<c>Plugin\</c>), else a repo's <c>Plugin\dist</c> above it (a developer's run).</summary>
        public static string? FindPluginDll()
        {
            string shipped = Path.Combine(AppContext.BaseDirectory, "Plugin", PluginFile);
            if (File.Exists(shipped)) return shipped;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string dev = Path.Combine(d.FullName, "Plugin", "dist", PluginFile);
                if (File.Exists(dev)) return dev;
            }
            return null;
        }

        public static bool SameFile(string a, string b)
        {
            try
            {
                if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
                using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
                return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>What an installation holds now, for the picker: not installed, the same build as ours, or another build.</summary>
        public static string StatusOf(FlStudioInstall target, string? dll)
        {
            if (!File.Exists(target.PluginPath)) return "not installed";
            if (dll != null && SameFile(target.PluginPath, dll)) return "installed, up to date";
            return "installed, a different build";
        }

        /// <summary>Installs into one FL Studio as the current user. A refusal for lack of rights is reported as <see cref="InstallOutcome.NeedsAdmin"/>, not an error.</summary>
        public static InstallResult TryInstall(FlStudioInstall target, string dll)
        {
            string dest = target.PluginPath, tmp = dest + ".new";
            try
            {
                if (File.Exists(dest) && SameFile(dest, dll)) return new(target, InstallOutcome.AlreadyCurrent, "already installed and identical");
                if (FlStudioLocator.IsRunning(target))
                    return new(target, InstallOutcome.FlRunning, "FL Studio is running from this installation: close it first (it keeps the plugin locked, and only looks for plugins at start-up)");

                Directory.CreateDirectory(target.PluginDir);
                // copy beside the target and rename over it: an interrupted copy must never leave a truncated DLL for FL to load
                try { File.Copy(dll, tmp, overwrite: true); File.Move(tmp, dest, overwrite: true); }
                finally { try { File.Delete(tmp); } catch (Exception) { } }

                return SameFile(dest, dll)
                    ? new(target, InstallOutcome.Installed, "installed")
                    : new(target, InstallOutcome.Failed, "copied, but the installed file does not match");
            }
            catch (UnauthorizedAccessException) { return new(target, InstallOutcome.NeedsAdmin, "needs administrator rights"); }
            catch (IOException ex) { return new(target, InstallOutcome.Failed, ex.Message + " (is FL Studio running?)"); }
            catch (Exception ex) { return new(target, InstallOutcome.Failed, ex.Message); }
        }

        /// <summary>
        /// Installs into every target. What needs administrator rights is done by one elevated copy of this program (a single UAC prompt)
        /// when <paramref name="allowElevation"/>; its outcome is judged by looking at the files, not by taking its word.
        /// </summary>
        public static List<InstallResult> InstallAll(IReadOnlyList<FlStudioInstall> targets, string dll, bool allowElevation)
        {
            var results = targets.Select(t => TryInstall(t, dll)).ToList();
            var needAdmin = results.Where(r => r.Outcome == InstallOutcome.NeedsAdmin).Select(r => r.Target).ToList();
            if (needAdmin.Count == 0 || !allowElevation) return results;

            string resultFile = Path.Combine(Path.GetTempPath(), $"bn2-install-{Guid.NewGuid():N}.txt");
            var args = new List<string> { "--install-vst", "--elevated", "--quiet", "--result", SelfLaunch.Quote(resultFile), "--dll", SelfLaunch.Quote(dll) };
            foreach (var t in needAdmin) { args.Add("--fl"); args.Add(SelfLaunch.Quote(t.Root)); }

            bool declined = false;
            string? startError = null;
            int exitCode = -1;
            try
            {
                using var child = SelfLaunch.Start(string.Join(' ', args), elevate: true);
                if (child == null) declined = true;
                else { child.WaitForExit(); exitCode = child.ExitCode; }
            }
            catch (Exception ex) { startError = ex.Message; }

            var detail = ReadResultFile(resultFile);
            try { File.Delete(resultFile); } catch (Exception) { }

            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].Outcome != InstallOutcome.NeedsAdmin) continue;
                var t = results[i].Target;
                if (declined) results[i] = new(t, InstallOutcome.Cancelled, "administrator permission was declined");
                else if (startError != null) results[i] = new(t, InstallOutcome.Failed, "could not start the administrator install: " + startError);
                else if (File.Exists(t.PluginPath) && SameFile(t.PluginPath, dll)) results[i] = new(t, InstallOutcome.Installed, "installed (with administrator rights)");
                else results[i] = new(t, InstallOutcome.Failed, detail.TryGetValue(t.Root, out var why) ? why : $"the administrator install did not complete (exit code {exitCode})");
            }
            return results;
        }

        /// <summary>The elevated child's report, one line per target: outcome, root, message (tab separated). Only a hint: the parent re-checks the files.</summary>
        public static void WriteResultFile(string path, IEnumerable<InstallResult> results)
        {
            try { File.WriteAllLines(path, results.Select(r => $"{r.Outcome}\t{r.Target.Root}\t{r.Message}")); } catch (Exception) { }
        }

        private static Dictionary<string, string> ReadResultFile(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var p = line.Split('\t');
                    if (p.Length >= 3) d[p[1]] = p[2];
                }
            }
            catch (Exception) { }
            return d;
        }
    }
}
