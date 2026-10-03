using System;
using System.Linq;
using System.Windows.Forms;

namespace BrokenNes.Windows.Synth
{
    /// <summary>
    /// The two ways to run BrokenNes 2: as the emulator (the default) or as a standalone synthesizer (<c>BrokenNes.Windows.exe --synth</c>).
    /// A mode is a property of the process, not a saved preference: "Restart as Standalone Synth" starts the program again with --synth and
    /// closes this one; "Restart as Emulator" is the way back. Nothing is remembered between launches, so a plain start is always the emulator.
    /// </summary>
    internal static class SynthMode
    {
        public const string Arg = "--synth";

        public static bool IsRequested(string[] args) => args.Any(a => a.Equals(Arg, StringComparison.OrdinalIgnoreCase));

        /// <summary>The <c>--synth</c> entry point: no emulator, the synth window only.</summary>
        public static int Run(string[] args)
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("The standalone synthesizer hit an error:\n\n" + e.Exception.Message, "BrokenNes 2 - Standalone Synthesizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new SynthForm());
            return 0;
        }

        /// <summary>Config > Synthesizer Mode > Restart as Standalone Synth. Refuses, with the reason, when this build has no plugin to host.</summary>
        public static void RestartAsSynth(Form emulatorWindow)
        {
            if (PluginInstaller.FindPluginDll() == null)
            {
                MessageBox.Show(emulatorWindow,
                    "The standalone synth is the FL Studio plugin hosted in its own window, and this build was made without the plugin (Plugin\\" + PluginInstaller.PluginFile + ").\n\n" +
                    "Build it with Plugin\\build-dist.ps1, then rebuild the desktop app.",
                    "Restart as Standalone Synth", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { SelfLaunch.Start(Arg); }
            catch (Exception ex)
            {
                MessageBox.Show(emulatorWindow, "Could not start the synthesizer:\n\n" + ex.Message, "Restart as Standalone Synth", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            emulatorWindow.Close();   // the normal way out: battery saves, continue state, the control API shuts down
        }

        /// <summary>The synth's way back.</summary>
        public static void RestartAsEmulator(Form synthWindow)
        {
            try { SelfLaunch.Start(""); }
            catch (Exception ex)
            {
                MessageBox.Show(synthWindow, "Could not start the emulator:\n\n" + ex.Message, "Restart as Emulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            synthWindow.Close();
        }
    }
}
