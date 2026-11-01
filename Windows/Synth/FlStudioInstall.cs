using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BrokenNes.Windows.Synth
{
    /// <summary>
    /// One FL Studio installation that the BrokenNes 2 plugin can be installed into. FL loads a native ("Fruity") generator from
    /// <c>&lt;root&gt;\Plugins\Fruity\Generators\&lt;Name&gt;\&lt;Name&gt;_x64.dll</c>, and the folder is only ever the installation's own.
    /// </summary>
    internal sealed record FlStudioInstall(string Name, string Root, int Edition)
    {
        /// <summary>The only FL Studio the plugin has been certified in (headless render, pitch within 0.1 cent). Others may well work; nobody has checked.</summary>
        public const int CertifiedEdition = 2026;

        public bool Certified => Edition == CertifiedEdition;
        public string GeneratorsDir => Path.Combine(Root, "Plugins", "Fruity", "Generators");
        public string PluginDir => Path.Combine(GeneratorsDir, PluginInstaller.PluginFolder);
        public string PluginPath => Path.Combine(PluginDir, PluginInstaller.PluginFile);

        public override string ToString() => Certified ? Name : $"{Name} (not certified)";
    }

    internal static class FlStudioLocator
    {
        private static readonly Regex Edition = new(@"(\d+)\s*$", RegexOptions.Compiled);

        /// <summary>Is this folder an FL Studio installation? Its own executable, or the plugin tree FL scans, is there.</summary>
        public static bool LooksLikeFl(string root) =>
            File.Exists(Path.Combine(root, "FL64.exe")) || Directory.Exists(Path.Combine(root, "Plugins", "Fruity"));

        /// <summary>The installation rooted at <paramref name="root"/> (a folder the user picked, or --fl), or null when it is not one.</summary>
        public static FlStudioInstall? FromFolder(string root)
        {
            try
            {
                root = Path.GetFullPath(root).TrimEnd('\\', '/');
                if (!Directory.Exists(root) || !LooksLikeFl(root)) return null;
                string name = new DirectoryInfo(root).Name;
                var m = Edition.Match(name);
                return new FlStudioInstall(name, root, m.Success ? int.Parse(m.Groups[1].Value) : 0);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Every FL Studio found in the usual Image-Line folders, newest first. (FL's own registry keys hold no install path, so the
        /// folders are the evidence; "FL Studio ASIO", "Minihost" and "Shared" sit beside the installs and are not FL.)
        /// </summary>
        public static List<FlStudioInstall> Detect()
        {
            var roots = new List<string>();
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                         .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string dir = Path.Combine(pf, "Image-Line");
                if (!Directory.Exists(dir)) continue;
                try { roots.AddRange(Directory.GetDirectories(dir, "FL Studio*")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return roots.Select(FromFolder).OfType<FlStudioInstall>()
                        .Where(i => File.Exists(Path.Combine(i.Root, "FL64.exe")))
                        .OrderByDescending(i => i.Edition)   // 21 sorts below 2024, which is the right way round
                        .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }

        /// <summary>
        /// Is FL Studio running from this installation? A running FL keeps the plugin DLL locked, and only looks for plugins at start-up.
        /// An FL process whose path cannot be read (it runs elevated) counts as running: refusing is cheaper than a half-copied DLL.
        /// </summary>
        public static bool IsRunning(FlStudioInstall install)
        {
            string prefix = install.Root.TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcessesByName("FL64"))
            {
                try
                {
                    string? file = p.MainModule?.FileName;
                    if (file == null || file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Win32Exception) { return true; }
                catch (InvalidOperationException) { /* it exited while we looked */ }
                finally { p.Dispose(); }
            }
            return false;
        }
    }
}
