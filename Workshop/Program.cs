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
        if (args.Length > 0 && args[0].Equals("--accuracycoin", StringComparison.OrdinalIgnoreCase))
        {
            return AccuracyCoinCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--playmovie", StringComparison.OrdinalIgnoreCase))
        {
            return TasCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--selfplay", StringComparison.OrdinalIgnoreCase))
        {
            return SelfPlayCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--verify-selfplay-movie", StringComparison.OrdinalIgnoreCase))
        {
            return MovieVerifyCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--diag-savestate-roundtrip", StringComparison.OrdinalIgnoreCase))
        {
            return SaveStateRoundtripDiagCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--benchmark", StringComparison.OrdinalIgnoreCase))
        {
            return BenchmarkCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--romtest", StringComparison.OrdinalIgnoreCase))
        {
            return RomTestCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--snestest", StringComparison.OrdinalIgnoreCase))
        {
            return SnesTestCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--snesrun", StringComparison.OrdinalIgnoreCase))
        {
            return SnesRunCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--snes", StringComparison.OrdinalIgnoreCase))
        {
            return SnesPlayerForm.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--trace", StringComparison.OrdinalIgnoreCase))
        {
            return TraceCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--apu-audio", StringComparison.OrdinalIgnoreCase))
        {
            return ApuAudioCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--corrupt", StringComparison.OrdinalIgnoreCase))
        {
            return VrunCorruptCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--irqtrace", StringComparison.OrdinalIgnoreCase))
        {
            return IrqTraceCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--compare-dumps", StringComparison.OrdinalIgnoreCase))
        {
            return DumpCompareCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--index-tas-library", StringComparison.OrdinalIgnoreCase))
        {
            return IndexTasLibraryCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--tas-baseline-batch", StringComparison.OrdinalIgnoreCase))
        {
            return TasBaselineBatchCli.Run(args);
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
