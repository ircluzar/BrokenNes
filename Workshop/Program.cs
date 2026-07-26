using System;
using System.Windows.Forms;

namespace BrokenNes.Workshop;

internal static class Program
{
    /// <summary>
    /// Two entry points sharing the exact same linked cores: an interactive WinForms debug UI
    /// (default), and a headless mode for scripted/automated accuracy runs (--headless ...).
    /// Deliberately one executable, not two projects - see HeadlessRunner for why.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--headless", StringComparison.OrdinalIgnoreCase))
        {
            return HeadlessRunner.Run(args);
        }

        ApplicationConfiguration.Initialize();

        // A debug tool that silently vanishes on a crash defeats its own purpose - show the
        // exception instead of letting WinForms' default handler just close the window.
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "Workshop crashed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject?.ToString() ?? "(no details)", "Workshop crashed", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new WorkshopForm());
        return 0;
    }
}
