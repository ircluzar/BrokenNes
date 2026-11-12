using System;
using System.IO;
using System.Windows.Forms;
using BrokenNes2.Extract;
using BrokenNes2.Instruments;
using NesEmulator;
using NesEmulator.Gb;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Instrument extraction (2026-11-10): Emulator > Capture Instrument > (channel). While the game plays, the shared capture core (Plugin/BrokenNes2.Plugin/Extract, linked) watches the channel
    /// from the click on; when the channel's next note has ended (key-off, the next note, or 10 s), the note is a BrokenNes2 preset (*.bn2preset) and a Save dialog exports it. NES (FIX APU),
    /// Game Boy, and (2026-11-12) SNES (the S-DSP's eight voices), Master System / Game Gear (PSG, YM2413) and Genesis (YM2612, DAC, PSG). The capture is defined in docs/projects/flp-music-pipeline.md ("Instrument extraction"). Nothing is changed in the game: the feed only observes the chip's register writes.
    /// </summary>
    public partial class MainForm
    {
        private IExtractFeed? extractFeed;
        private object? extractMachine;
        private int extractChannel, extractFrom, extractTimeout;
        private ToolStripMenuItem? extractMenu;
        private const int ExtractMaxFrames = 600;

        private void AddExtractMenu(ToolStripMenuItem emulatorMenu)
        {
            extractMenu = new ToolStripMenuItem("Capture &Instrument") { ToolTipText = "Capture the next note of a sound channel as a BrokenNes2 preset (.bn2preset) for the FL Studio plugin" };
            emulatorMenu.DropDownItems.Add(extractMenu);
            emulatorMenu.DropDownOpening += (_, _) => RefreshExtractMenu();
            RefreshExtractMenu();
        }

        private void RefreshExtractMenu()
        {
            if (extractMenu == null) return;
            extractMenu.DropDownItems.Clear();
            var console = ExtractConsole(out _);
            if (console == null) { extractMenu.DropDownItems.Add(new ToolStripMenuItem("(a NES game on the FIX sound chip, or a Game Boy, SNES, Master System, Game Gear or Genesis game)") { Enabled = false }); return; }
            if (extractFeed != null)
            {
                extractMenu.DropDownItems.Add(new ToolStripMenuItem($"Waiting for a note on {CapturedNote.ChannelName(console.Value, extractChannel)}...") { Enabled = false });
                extractMenu.DropDownItems.Add(new ToolStripMenuItem("Cancel", null, (_, _) => StopExtract()));
                return;
            }
            int channels = console is ConsoleId.Nes or ConsoleId.GameBoy ? 4 : session != null ? ExtractChips.OfferedChannels(session) : 4;
            for (int c = 0; c < channels; c++)
            {
                int ch = c;
                extractMenu.DropDownItems.Add(new ToolStripMenuItem(CapturedNote.ChannelName(console.Value, c), null, (_, _) => StartExtract(ch)));
            }
        }

        /// <summary>The console extraction can capture now (null: none), and the machine it reads.</summary>
        private ConsoleId? ExtractConsole(out object? machine)
        {
            machine = null;
            if (session is GbSession gb && gb.Board.Apu is APU_GB) { machine = gb; return ConsoleId.GameBoy; }
            if (session == null && nes != null && NesExtractFeed.FixOf(nes) != null) { machine = nes; return ConsoleId.Nes; }
            if (session != null && ExtractChips.ConsoleOf(session) is { } c) { machine = session; return c; }
            return null;
        }

        private void StartExtract(int channel)
        {
            lock (emulationLock)
            {
                StopExtractLocked();
                var console = ExtractConsole(out var machine);
                if (console == null) { MessageBox.Show(this, "Instrument capture needs a NES game on the FIX sound chip, or a Game Boy, SNES, Master System, Game Gear or Genesis game.", "Capture Instrument"); return; }
                extractFeed = machine switch
                {
                    GbSession gb => new GbExtractFeed((APU_GB)gb.Board.Apu),
                    IConsoleSession s => ExtractChips.FeedFor(s)!,
                    _ => new NesExtractFeed(NesExtractFeed.FixOf((NES)machine!)!, ((NES)machine!).CopyApuLatch),
                };
                extractMachine = machine;
                extractChannel = channel;
                extractFrom = 0;
                extractTimeout = ExtractMaxFrames * 2;
            }
            Console.WriteLine($"[Extract] waiting for a note on channel {channel}");
        }

        private void StopExtract() { lock (emulationLock) StopExtractLocked(); }

        private void StopExtractLocked()
        {
            extractFeed?.Detach();
            extractFeed = null;
            extractMachine = null;
        }

        /// <summary>The emulation thread, after every frame (from TfAfterFrame): feeds the capture, and hands a finished note to the UI thread.</summary>
        private void ExtractAfterFrame(object machine)
        {
            var feed = extractFeed;
            if (feed == null) return;
            if (!ReferenceEquals(machine, extractMachine)) { StopExtractLocked(); return; }      // (another game was loaded)
            feed.EndFrame();
            var rec = feed.Recorder;
            var n = rec.Pick(extractChannel, Math.Min(extractFrom, rec.FrameCount - 1), out _);
            bool done = n != null && n.End != CaptureEnd.RecordingEnd;
            if (!done && --extractTimeout > 0) return;
            StopExtractLocked();
            string game = Path.GetFileNameWithoutExtension(currentRomPath ?? "");
            BeginInvoke(new Action(() => FinishExtract(n, game)));
        }

        private void FinishExtract(CapturedNote? n, string game)
        {
            if (n == null) { MessageBox.Show(this, "No note was played on that channel in the last 20 seconds.", "Capture Instrument"); return; }
            var file = CapturePreset.Write(n, "", game, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out string? problem);
            if (file == null) { MessageBox.Show(this, "The note could not be made a preset: " + problem, "Capture Instrument"); return; }
            using var dlg = new SaveFileDialog
            {
                Title = "Export the captured instrument",
                Filter = "BrokenNes2 preset (*.bn2preset)|*.bn2preset",
                FileName = $"{(game.Length > 0 ? game : "game")} - {CapturedNote.ChannelName(n.Console, n.Channel)} @ {n.KeyOnFrame}.bn2preset",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllBytes(dlg.FileName, file);
            Console.WriteLine($"[Extract] {n.Describe()} -> {dlg.FileName}");
        }
    }
}
