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
        if (args.Length > 0 && args[0].Equals("--mixlab", StringComparison.OrdinalIgnoreCase))
        {
            return MixLab.MixLabCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--sega-test", StringComparison.OrdinalIgnoreCase)) return Sega.SegaFoundationTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--sega-chips-test", StringComparison.OrdinalIgnoreCase)) return Sega.SegaChipsTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--z80test", StringComparison.OrdinalIgnoreCase)) return Sega.Z80TestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--m68ktest", StringComparison.OrdinalIgnoreCase)) return Sega.M68kTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-run", StringComparison.OrdinalIgnoreCase)) return Sega.MdRunCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-sweep", StringComparison.OrdinalIgnoreCase)) return Sega.MdSweepCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-oracle", StringComparison.OrdinalIgnoreCase)) return Sega.MdOracleCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-cart-test", StringComparison.OrdinalIgnoreCase)) return Sega.MdCartTest.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-state-test", StringComparison.OrdinalIgnoreCase)) return Sega.MdStateTest.Run(args);
        if (args.Length > 0 && args[0].Equals("--snes-state-test", StringComparison.OrdinalIgnoreCase)) return SnesStateTest.Run(args);
        if (args.Length > 0 && args[0].Equals("--state-audit", StringComparison.OrdinalIgnoreCase)) return StateAuditCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--alloc-probe", StringComparison.OrdinalIgnoreCase)) return AllocProbeCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-save-test", StringComparison.OrdinalIgnoreCase)) return Sega.MdSaveTest.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-render", StringComparison.OrdinalIgnoreCase)) return Sega.MdRenderCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--img-diff", StringComparison.OrdinalIgnoreCase)) return Sega.ImgDiffCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-find", StringComparison.OrdinalIgnoreCase)) return Sega.MdFindCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-scenes", StringComparison.OrdinalIgnoreCase)) return Sega.MdSceneCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-census", StringComparison.OrdinalIgnoreCase)) return Sega.MdCensusCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-pic-test", StringComparison.OrdinalIgnoreCase)) return Sega.MdPictureTest.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-golden", StringComparison.OrdinalIgnoreCase)) return Sega.MdGoldenCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--sega-bench", StringComparison.OrdinalIgnoreCase)) return Sega.SegaBenchCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-test", StringComparison.OrdinalIgnoreCase)) return Sega.MdBoardTest.Run(args);
        if (args.Length > 0 && args[0].Equals("--sega-contracts-test", StringComparison.OrdinalIgnoreCase)) return Sega.SegaContractsTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--sega-oracle-test", StringComparison.OrdinalIgnoreCase)) return Sega.SegaOracleTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--sega-bridge-test", StringComparison.OrdinalIgnoreCase)) return Sega.SegaBridgeTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--picture-bridge", StringComparison.OrdinalIgnoreCase)) return Sega.PictureBridgeCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--picture-bridge-test", StringComparison.OrdinalIgnoreCase)) return Sega.PictureBridgeTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--bridge-sweep", StringComparison.OrdinalIgnoreCase)) return Sega.BridgeSweepCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--bridge-sweep-one", StringComparison.OrdinalIgnoreCase)) return Sega.BridgeSweepCli.RunOne(args);
        if (args.Length > 0 && args[0].Equals("--cpu-wall-test", StringComparison.OrdinalIgnoreCase)) return Sega.CpuWallTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--session-test", StringComparison.OrdinalIgnoreCase)) return Sega.SessionTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--swap-matrix-test", StringComparison.OrdinalIgnoreCase)) return Sega.SwapMatrixTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-bridge-scenes", StringComparison.OrdinalIgnoreCase)) return Sega.MdBridgeScenesCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-patches", StringComparison.OrdinalIgnoreCase)) return Sega.MdPatchCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--sega-stems-test", StringComparison.OrdinalIgnoreCase)) return Sega.SegaStemsTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--ym-oracle-test", StringComparison.OrdinalIgnoreCase)) return Sega.YmOracleTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--ym-render", StringComparison.OrdinalIgnoreCase)) return Sega.YmRenderCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--md-sound-bench", StringComparison.OrdinalIgnoreCase)) return Sega.MdSoundBenchCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--sms-run", StringComparison.OrdinalIgnoreCase)) return Sega.SmsRunCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--smstest", StringComparison.OrdinalIgnoreCase)) return Sega.SmsTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--sms-sweep", StringComparison.OrdinalIgnoreCase)) return Sega.SmsSweepCli.Run(args);
        if (args.Length > 0 && (args[0].Equals("--fm-oracle-test", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--fm-dump", StringComparison.OrdinalIgnoreCase))) return Sega.FmOracleTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--brokendev", StringComparison.OrdinalIgnoreCase)) return BrokenDevCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--corrupt-test", StringComparison.OrdinalIgnoreCase)) return Corruption.CorruptCli.Test(args);
        if (args.Length > 0 && args[0].Equals("--corrupt-apply", StringComparison.OrdinalIgnoreCase)) return Corruption.CorruptCli.Apply(args);
        if (args.Length > 0 && args[0].Equals("--input-test", StringComparison.OrdinalIgnoreCase)) return InputTestCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--console-catalog", StringComparison.OrdinalIgnoreCase)) return ConsoleRunCli.Catalog();
        if (args.Length > 0 && args[0].Equals("--console-run", StringComparison.OrdinalIgnoreCase)) return ConsoleRunCli.Run(args);
        if (args.Length > 0 && args[0].Equals("--snesrun", StringComparison.OrdinalIgnoreCase))
        {
            return SnesRunCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--dsp1-lab", StringComparison.OrdinalIgnoreCase))
        {
            return Dsp1LabCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--snesbench", StringComparison.OrdinalIgnoreCase))
        {
            return SnesBenchCli.Run(args);
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
        if (args.Length > 0 && args[0].Equals("--gbtest", StringComparison.OrdinalIgnoreCase))
        {
            return Gb.GbTestCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--gbrun", StringComparison.OrdinalIgnoreCase))
        {
            return Gb.GbRunCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--gbsweep", StringComparison.OrdinalIgnoreCase))
        {
            return Gb.GbSweepCli.Run(args);
        }
        if (args.Length > 0 && args[0].Equals("--gb", StringComparison.OrdinalIgnoreCase))
        {
            return Gb.GbPlayerForm.Run(args);
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
