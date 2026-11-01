using System.Windows.Forms;
using BrokenNes.Windows.Synth;

namespace BrokenNes.Windows
{
    // Config > Synthesizer Mode: BrokenNes 2 as an instrument (the plugin) instead of an emulator.
    public partial class MainForm
    {
        private ToolStripMenuItem BuildSynthesizerMenu()
        {
            var menu = new ToolStripMenuItem("S&ynthesizer Mode");
            menu.DropDownItems.Add(new ToolStripMenuItem("Restart as &Standalone Synth", null, (s, e) => RestartAsSynth_Click()));
            menu.DropDownItems.Add(new ToolStripMenuItem("&Install to FL Studio...", null, (s, e) => InstallToFlStudio_Click()));
            return menu;
        }

        /// <summary>Starts this program again as a standalone synthesizer (<c>--synth</c>) and closes the emulator. "Restart as Emulator" in the synth is the way back.</summary>
        private void RestartAsSynth_Click() => SynthMode.RestartAsSynth(this);

        /// <summary>Which FL Studio? (several can be installed), then the copy with its UAC prompt. The same flow as <c>--install-vst</c>.</summary>
        private void InstallToFlStudio_Click() => InstallVstFlow.RunInteractive(this);
    }
}
