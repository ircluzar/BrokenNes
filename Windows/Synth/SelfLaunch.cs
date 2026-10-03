using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace BrokenNes.Windows.Synth
{
    /// <summary>Starts another copy of this very program with other arguments: the synth restart, and the elevated half of the plugin install.</summary>
    internal static class SelfLaunch
    {
        public const int ErrorCancelled = 1223;   // ERROR_CANCELLED: the user declined the UAC prompt

        public static string Quote(string s) => "\"" + s + "\"";

        /// <summary>
        /// Starts this program with <paramref name="arguments"/>. With <paramref name="elevate"/> Windows shows the UAC prompt first.
        /// Returns null when the user declined that prompt. A real failure to start throws.
        /// </summary>
        public static Process? Start(string arguments, bool elevate = false)
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot tell which program is running");
            // `dotnet BrokenNes.Windows.dll` (a developer run): the host is dotnet.exe, so the program's own dll has to come first
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                arguments = Quote(Path.Combine(AppContext.BaseDirectory, "BrokenNes.Windows.dll")) + " " + arguments;
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            if (elevate) { psi.Verb = "runas"; psi.WindowStyle = ProcessWindowStyle.Hidden; }
            try { return Process.Start(psi); }
            catch (Win32Exception ex) when (elevate && ex.NativeErrorCode == ErrorCancelled) { return null; }
        }
    }
}
