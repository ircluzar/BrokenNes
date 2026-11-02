using System;
using System.IO;
using System.Windows.Forms;
using BrokenNes.Windows.Virtual;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// WideMess Virtual (docs/projects/next-pool-plans.md, P4): Load ROM accepts a <c>.wmv</c> virtual cartridge and runs it as the WideMess console's game
    /// (<see cref="VirtualCartSession"/>). It goes through the console session path like any game: the picture is the session's 400 x 224 frame (so the viewport,
    /// scaling and shaders apply), the pad is the player's pad, pause stops the frames (and the session freezes the page). What needs a machine (save states,
    /// Timeflow, core swaps, corruption) is greyed out or refused with <see cref="VirtualCartSession.NoMachineReason"/>. Ejecting (another game, Close ROM,
    /// exit) disposes the session, which stops the game's server; if the server or the page dies, the cartridge is ejected and the user is told why.
    /// </summary>
    public partial class MainForm
    {
        private System.Windows.Forms.Timer? virtualCartWatch;

        /// <summary>The web view profile of the virtual cartridges: beside the config (a scratch BROKENNES_CONFIG_DIR keeps tests away from the user's).</summary>
        private static string VirtualCartWebViewFolder()
        {
            string? cfg = Environment.GetEnvironmentVariable("BROKENNES_CONFIG_DIR");
            string root = !string.IsNullOrEmpty(cfg) ? cfg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes");
            return Path.Combine(root, "WebView2-virtual");
        }

        /// <summary>Starts a virtual cartridge on the WideMess. True when it runs; otherwise the reason is shown (or, from the HTTP API, logged) and the idle screen comes back.</summary>
        private bool StartVirtualCart(string path, bool showDialogs)
        {
            HideContinueButton();
            if (!Consoles.IsAvailable(ConsoleKind.WideMess)) return Fail("The WideMess console is not offered in this build.");
            VirtualCartManifest m;
            try { m = VirtualCartManifest.Load(path); }
            catch (InvalidDataException ex) { return Fail(ex.Message); }

            StopEmulation();
            StopSession();
            lock (emulationLock)
            {
                SaveBatteryRamForNes(nes);
                nes = null;
            }
            VirtualCartSession s;
            try
            {
                Cursor = Cursors.WaitCursor;
                s = new VirtualCartSession(m, VirtualCartWebViewFolder());
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
            {
                LoadEmbeddedRom(allowHomeWebModule: false);
                return Fail(ex.Message);
            }
            finally { Cursor = Cursors.Default; }

            byte[] manifestBytes = File.ReadAllBytes(m.ManifestPath);
            session = s; sessionRom = manifestBytes; sessionRomName = Path.GetFileName(m.ManifestPath); sessionFrames = 0; consoleNote = "";
            currentRomPath = m.ManifestPath;
            Helpers.ConfigHelper.Update(config, c => c.SelectedConsole = Consoles.Key(ConsoleKind.WideMess));
            config.AddRecentRom(m.ManifestPath);
            RefreshConsoleMenu();
            UpdateCoresMenus();
            UpdateConsoleTitle();
            RefreshRecentRomsMenu();
            Console.WriteLine($"[WideMess Virtual] {s.Description}");
            StartEmulation();
            WatchVirtualCart();
            return true;

            bool Fail(string why)
            {
                Console.WriteLine($"[WideMess Virtual] {why}");
                if (showDialogs) MessageBox.Show(this, why, "WideMess virtual cartridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        /// <summary>Once a second: if the cartridge's server or page has died, eject it and say why (never leave a dead picture pretending to run).</summary>
        private void WatchVirtualCart()
        {
            if (virtualCartWatch == null)
            {
                virtualCartWatch = new System.Windows.Forms.Timer { Interval = 1000 };
                virtualCartWatch.Tick += (_, _) =>
                {
                    if (session is not VirtualCartSession v) { virtualCartWatch.Stop(); return; }
                    if (v.Problem is not { } why) return;
                    virtualCartWatch.Stop();
                    string title = v.Title;
                    StopSession();
                    LoadEmbeddedRom(allowHomeWebModule: false);
                    UpdateCoresMenus();
                    UpdateConsoleTitle();
                    Console.WriteLine($"[WideMess Virtual] {title} ejected: {why}");
                    MessageBox.Show(this, $"{title} stopped and was ejected: {why}", "WideMess virtual cartridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };
            }
            virtualCartWatch.Start();
        }
    }
}
