using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Save states of the console sessions (Master System, Game Gear and any other session that supports them): the same menu items and keys the NES has (Quick Save F7, Quick Load F5, Save State..., Load State...),
    /// reaching these methods only while a session runs. The NES's own JSON states are untouched. A quick slot is kept per game on disk (<c>%APPDATA%\BrokenNes\States\&lt;console&gt;\&lt;sha1&gt;.quick.bnstate</c>), so it
    /// survives closing the app; a session without states says so (a message, or the console when called from the API) rather than doing nothing.
    /// </summary>
    public partial class MainForm
    {
        private System.Windows.Forms.Timer? stateFlashTimer;

        private string SessionStateRoot => AppDataDir;

        /// <summary>Quick-saves the running session. False (with the reason shown when <paramref name="showDialogs"/>) when there is none, it has no states, or the save failed.</summary>
        private bool TrySessionQuickSave(bool showDialogs)
        {
            var s = session;
            if (s == null) return false;
            if (!s.SupportsState)
            {
                Refuse(showDialogs, "Quick Save", s.StateUnsupportedReason);
                return false;
            }
            try
            {
                byte[] state;
                lock (emulationLock) state = s.SaveState();   // between frames: the emulation thread runs frames under this lock
                string path = ConsoleStateFile.SlotPath(SessionStateRoot, s.Console, s.GameId);
                WriteStateFile(path, state);
                Console.WriteLine($"Quick Saved: {s.Title} ({state.Length / 1024} KB)");
                FlashTitle("state saved");
                return true;
            }
            catch (Exception ex)
            {
                Refuse(showDialogs, "Quick Save", "Could not save the state: " + ex.Message);
                return false;
            }
        }

        /// <summary>Quick-loads the game's slot. A state for another game, console or sound unit is refused with the reason and the game keeps running as it was.</summary>
        private bool TrySessionQuickLoad(bool showDialogs)
        {
            var s = session;
            if (s == null) return false;
            if (!s.SupportsState)
            {
                Refuse(showDialogs, "Quick Load", s.StateUnsupportedReason);
                return false;
            }
            string path = ConsoleStateFile.SlotPath(SessionStateRoot, s.Console, s.GameId);
            if (!File.Exists(path))
            {
                Refuse(showDialogs, "Quick Load", "No quick save state for this game yet. Use Quick Save State (F7) first.");
                return false;
            }
            return LoadSessionStateFile(s, path, showDialogs, "Quick Load");
        }

        private bool LoadSessionStateFile(IConsoleSession s, string path, bool showDialogs, string caption)
        {
            try
            {
                byte[] state = File.ReadAllBytes(path);
                lock (emulationLock) { s.LoadState(state); farm.Poison("a saved state was loaded"); }
                Console.WriteLine($"State loaded: {s.Title}");
                FlashTitle("state loaded");
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
            {
                Refuse(showDialogs, caption, ex.Message);
                return false;
            }
        }

        /// <summary>Save State... for a running session: a file of the user's choosing.</summary>
        private void SaveSessionStateToFile()
        {
            var s = session;
            if (s == null) return;
            if (!s.SupportsState) { Refuse(true, "Save State", s.StateUnsupportedReason); return; }
            using var dialog = new SaveFileDialog
            {
                Filter = "BrokenNes console states (*.bnstate)|*.bnstate",
                Title = "Save State",
                DefaultExt = "bnstate",
                FileName = SafeFileName(s.Title) + ".bnstate",
            };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            try
            {
                byte[] state;
                lock (emulationLock) state = s.SaveState();
                WriteStateFile(dialog.FileName, state);
                Console.WriteLine($"State saved: {dialog.FileName}");
                FlashTitle("state saved");
            }
            catch (Exception ex) { Refuse(true, "Save State", "Could not save the state: " + ex.Message); }
        }

        /// <summary>Load State... for a running session.</summary>
        private void LoadSessionStateFromFile()
        {
            var s = session;
            if (s == null) return;
            if (!s.SupportsState) { Refuse(true, "Load State", s.StateUnsupportedReason); return; }
            using var dialog = new OpenFileDialog { Filter = "BrokenNes console states (*.bnstate)|*.bnstate|All files (*.*)|*.*", Title = "Load State" };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            LoadSessionStateFile(s, dialog.FileName, showDialogs: true, "Load State");
        }

        private static void WriteStateFile(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }

        private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        /// <summary>The message of the last refused save or load (what the HTTP quick-save answer gives as its reason).</summary>
        private string? lastStateMessage;

        private void Refuse(bool showDialogs, string caption, string message)
        {
            lastStateMessage = message;
            Console.WriteLine($"[{caption}] {message}");
            if (showDialogs) MessageBox.Show(this, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Shows a short note in the window title for two seconds (the NES's quick save gives no other sign).</summary>
        private void FlashTitle(string note)
        {
            void Apply()
            {
                UpdateConsoleTitle();
                Text += $" - {note}";
                stateFlashTimer?.Dispose();
                stateFlashTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                stateFlashTimer.Tick += (_, _) => { stateFlashTimer?.Stop(); UpdateConsoleTitle(); };
                stateFlashTimer.Start();
            }
            if (InvokeRequired) BeginInvoke(Apply); else Apply();
        }
    }
}
