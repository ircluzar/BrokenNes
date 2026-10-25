using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NesEmulator;
using NesEmulator.Debugging.Capture;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// The bug capture (BrokenDev milestone D6, docs/projects/brokendev-debugging-plan.md): the app keeps the last 30 s of the running game
    /// (<see cref="BugCaptureRing"/>: a save state, a picture and the keyframe hashes a second, every frame's pads, the sound) and F9 (Emulator >
    /// Capture Bug) saves them as a bundle: in the game's BrokenDev workspace <c>bugs\</c> folder when the ROM is a BrokenDev project's build, else
    /// in <c>%LOCALAPPDATA%\BrokenNes\bugs</c> (<see cref="BugPlaces"/>). Off (Emulator > Keep the Last 30 s, or <c>bugCaptureEnabled</c> false),
    /// nothing of it runs: the frame loop's calls return at their first test. Everything the ring does runs on the emulation thread, between frames.
    /// </summary>
    public partial class MainForm
    {
        private readonly BugCaptureRing bugRing = new();
        private object? bugAttachedTo;               // the NES or the session the ring records
        private volatile bool bugPowerOn;            // the next game's first frame is its power-on
        private string? bugPendingBreak;             // a change outside the game, applied before the next frame (Interlocked)
        private byte[]? bugRom;                      // the ROM the running game was started from (a .zip's inner ROM)
        private string bugRomName = "";
        private volatile bool bugCaptureBusy;
        private string bugLastSaved = "", bugLastWhy = "";

        private bool BugCaptureOn => config.BugCaptureEnabled;

        /// <summary>A game was loaded (the NES or a session): the ring starts again at its power-on.</summary>
        private void BugGameLoaded(byte[] rom, string romName)
        {
            bugRom = rom; bugRomName = romName;
            bugPowerOn = true;
        }

        /// <summary>
        /// Something outside the game changed it (<paramref name="why"/>): the history starts again before the next frame. Any thread; the
        /// ring applies it on the emulation thread.
        /// </summary>
        internal void BugBreak(string why)
        {
            if (!BugCaptureOn) return;
            Interlocked.Exchange(ref bugPendingBreak, why);
        }

        private bool BugPrepare(object machine, Func<BugCaptureSource> source, double fps)
        {
            if (!BugCaptureOn)
            {
                if (bugRing.Attached) { bugRing.Detach(); bugAttachedTo = null; }
                bugPowerOn = false;
                return false;
            }
            if (!ReferenceEquals(bugAttachedTo, machine))
            {
                bugRing.Attach(source(), config.SoundQuality, fps, bugPowerOn);
                bugAttachedTo = machine;
                Interlocked.Exchange(ref bugPendingBreak, null);
            }
            bugPowerOn = false;
            if (Interlocked.Exchange(ref bugPendingBreak, null) is { } why) bugRing.Break(why);
            return true;
        }

        /// <summary>Before a NES frame (emulation thread): the pads the NES was just handed.</summary>
        private void BugBeforeNesFrame(NES n, bool[] p1, bool[] p2)
        {
            if (!BugCaptureOn && !bugRing.Attached) return;
            if (string.Equals(n.RomName, "test.nes", StringComparison.OrdinalIgnoreCase)) { if (bugRing.Attached) { bugRing.Detach(); bugAttachedTo = null; } return; }   // the idle screen
            if (!BugPrepare(n, () => BugCaptureSource.For(n), 60.0988)) return;
            bugRing.Frame(p1, p2);
        }

        /// <summary>Before a session's frame (emulation thread, the emulation lock held): its pads.</summary>
        private void BugBeforeSessionFrame(IConsoleSession s, PadButtons p1, PadButtons p2)
        {
            if (!BugCaptureOn && !bugRing.Attached) return;
            var c = s.Console;
            if (!BugPrepare(s, () => BugCaptureSource.For(s, () => SessionCores(c)), s.FramesPerSecond)) return;
            bugRing.Frame(p1, p2);
        }

        private string SessionCores(ConsoleKind c) => $"{SelectedCore(c, CoreSlot.Cpu)}/{SelectedCore(c, CoreSlot.Ppu)}/{SelectedCore(c, CoreSlot.Apu)}";

        /// <summary>The sound queued after a frame (emulation thread).</summary>
        private void BugAudio(float[] samples, int count)
        {
            if (bugRing.Attached) bugRing.Audio(samples, count);
        }

        // ------------------------------------------------------------------ the key, the menu

        private void AddBugCaptureMenu(ToolStripMenuItem emulatorMenu)
        {
            var capture = new ToolStripMenuItem("Capture &Bug (last 30 s)", null, (_, _) => _ = CaptureBugAsync(null, showDialogs: true)) { ShortcutKeys = Keys.F9 };
            capture.ToolTipText = "Saves the last 30 seconds (a movie that replays exactly, the sound, a picture a second) for BrokenDev: brokendev.ps1 bugs";
            var keep = new ToolStripMenuItem("Keep the Last 30 s for Bug Capture") { CheckOnClick = true, Checked = config.BugCaptureEnabled };
            keep.ToolTipText = "Off: nothing is recorded and F9 has nothing to save";
            keep.CheckedChanged += (_, _) => SetBugCaptureEnabled(keep.Checked);
            var open = new ToolStripMenuItem("Open Bugs Folder", null, (_, _) => OpenBugsFolder());
            emulatorMenu.DropDownOpening += (_, _) => keep.Checked = config.BugCaptureEnabled;
            emulatorMenu.DropDownItems.Add(capture);
            emulatorMenu.DropDownItems.Add(keep);
            emulatorMenu.DropDownItems.Add(open);
        }

        private void SetBugCaptureEnabled(bool on)
        {
            if (config.BugCaptureEnabled == on) return;
            Helpers.ConfigHelper.Update(config, c => c.BugCaptureEnabled = on);
            Console.WriteLine($"[Bugs] keeping the last 30 s: {(on ? "on" : "off")}");
        }

        /// <summary>The bugs folder of the running game (its BrokenDev workspace's, or the default one), and why.</summary>
        private string BugFolderNow(out string why, out BugProject? project, List<(string, byte[])>? symbols = null)
        {
            var rom = bugRom;
            if (rom == null) { why = "no game is running: the default folder"; project = null; return BugPlaces.DefaultFolder(); }
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rom)).ToLowerInvariant();
            return BugPlaces.For(sha, out project, out why, symbols);
        }

        private void OpenBugsFolder()
        {
            string dir = BugFolderNow(out string why, out _);
            try
            {
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
                Console.WriteLine($"[Bugs] {dir} ({why})");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bugs folder"); }
        }

        /// <summary>What the API's status route answers.</summary>
        internal object BugStatus()
        {
            string dir = BugFolderNow(out string why, out var project);
            return new
            {
                enabled = config.BugCaptureEnabled,
                recording = bugRing.Attached,
                console = bugRing.Attached ? bugRing.ConsoleKey : null,
                game = bugRing.Attached ? bugRomName : null,
                heldSeconds = Math.Round(bugRing.HeldSeconds, 2),
                frames = bugRing.FrameCount,
                breakReason = bugRing.BreakReason.Length > 0 ? bugRing.BreakReason : null,
                breakFrame = bugRing.BreakFrame >= 0 ? bugRing.BreakFrame : (long?)null,
                stateError = bugRing.StateError.Length > 0 ? bugRing.StateError : null,
                lastStateMs = Math.Round(bugRing.LastStateMs, 3),
                maxStateMs = Math.Round(bugRing.MaxStateMs, 3),
                lastStateBytes = bugRing.LastStateBytes,
                folder = dir, folderWhy = why, project = project?.Id,
                lastSaved = bugLastSaved.Length > 0 ? bugLastSaved : null,
            };
        }

        private void WireBugApi(WebApi.WebApiServer server)
        {
            server.ExternalChange = BugBreak;
            server.GetBugStatus = BugStatus;
            server.SetBugCaptureEnabled = on => SetBugCaptureEnabled(on);
            server.CaptureBug = async note =>
            {
                // on the UI thread like the key (the capture reads the form's state), without the note box
                var t = (Task<(string? path, string message, BugCaptureData? data)>)Invoke(new Func<Task<(string?, string, BugCaptureData?)>>(() => CaptureBugAsync(note, showDialogs: false)));
                var (path, message, d) = await t;
                return (path, message, d != null ? Math.Round(d.Seconds, 2) : 0, d?.Frames ?? 0,
                        d == null ? "" : d.NoMovieWhy != null ? "none" : d.Start == NesEmulator.Debugging.Movies.MovieStart.State ? "state" : "power-on", d?.NoMovieWhy);
            };
        }

        /// <summary>
        /// The capture: the ring's last 30 s copied on the emulation thread, then (with <paramref name="showDialogs"/>) the note asked with the
        /// game paused, then the bundle written off the UI thread. <paramref name="note"/>: the note when it is not asked (the HTTP API).
        /// Returns the bundle's folder, or null with <see cref="bugLastWhy"/> saying why.
        /// </summary>
        internal async Task<(string? path, string message, BugCaptureData? data)> CaptureBugAsync(string? note, bool showDialogs)
        {
            void Tell(string m) { bugLastWhy = m; Console.WriteLine("[Bugs] " + m); if (showDialogs) MessageBox.Show(this, m, "Capture Bug"); }
            if (bugCaptureBusy) { Tell("A capture is being saved; try again in a moment."); return (null, bugLastWhy, null); }
            if (!config.BugCaptureEnabled) { Tell("Bug capture is off (Emulator > Keep the Last 30 s for Bug Capture): nothing was recorded."); return (null, bugLastWhy, null); }
            if (!bugRing.Attached || (nes == null && session == null)) { Tell("No game is being recorded (the idle screen, or no game loaded)."); return (null, bugLastWhy, null); }
            if (!isEmulationRunning) { Tell("The emulation is stopped: nothing to capture."); return (null, bugLastWhy, null); }
            bugCaptureBusy = true;
            try
            {
                BugCaptureData data;
                var meta = new BugMeta { RomName = bugRomName, Rom = bugRom ?? Array.Empty<byte>(), RomPath = currentRomPath ?? "", App = AppBuild(), CapturedUtc = DateTime.UtcNow, CapturedLocal = DateTime.Now };
                var job = RunOnEmulationThreadAsync(() =>
                {
                    if (Interlocked.Exchange(ref bugPendingBreak, null) is { } why) bugRing.Break(why);
                    var d = bugRing.Capture();
                    // the machine the movie names, read at the same moment
                    if (nes != null)
                    {
                        meta.Cores = BugCaptureSource.NesCores(nes); meta.NtscTiming = BugCaptureSource.NesNtsc(nes);
                        meta.Settings.Add(("powerOnRam", brokenDevPowerOnRam.ToString().ToLowerInvariant()));
                    }
                    else if (session != null)
                    {
                        meta.Cores = SessionCores(session.Console);
                        if (SegaMachineOf(session) is { } sega) { meta.Settings.Add(("region", sega.region)); meta.Settings.Add(("model", sega.model)); }
                        meta.Settings.Add(("description", session.Description));
                    }
                    return d;
                });
                if (await Task.WhenAny(job, Task.Delay(5000)) != job)
                {
                    Tell("The emulation thread did not answer within 5 s (stopped at a breakpoint?): nothing was captured.");
                    return (null, bugLastWhy, null);
                }
                try { data = await job; }
                catch (InvalidOperationException e) { Tell("Nothing to capture: " + e.Message); return (null, bugLastWhy, null); }

                if (showDialogs)
                {
                    bool wasPaused = isPaused;
                    isPaused = true;
                    string? typed;
                    try { typed = AskBugNote(data); }
                    finally { isPaused = wasPaused; }
                    if (typed == null) { bugLastWhy = "the capture was discarded"; Console.WriteLine("[Bugs] discarded"); return (null, bugLastWhy, data); }
                    note = typed;
                }
                meta.Note = (note ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                var symbols = new List<(string, byte[])>();
                string dir = BugFolderNow(out string where, out var project, symbols);
                meta.Project = project;
                foreach (var s in symbols) meta.Symbols.Add(s);
                if (project != null) meta.Build = $"BrokenDev {project.Id}, built {project.BuildStarted}";
                string path = await Task.Run(() => BugBundle.WriteFolder(dir, BugBundle.Files(data, meta), meta.CapturedLocal));
                bugLastSaved = path;
                string msg = $"saved {data.Seconds:F1} s to {path} ({where}){(data.NoMovieWhy != null ? "; no movie: " + data.NoMovieWhy : "")}";
                bugLastWhy = msg;
                Console.WriteLine("[Bugs] " + msg);
                FlashTitle($"bug saved ({data.Seconds:F0} s)");
                return (path, msg, data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Tell("Could not save the capture: " + ex.Message);
                return (null, bugLastWhy, null);
            }
            finally { bugCaptureBusy = false; }
        }

        private static string AppBuild()
        {
            var a = typeof(MainForm).Assembly;
            string v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString() ?? "?";
            return $"BrokenNes desktop {v}";
        }

        /// <summary>The note box: the capture's length, a one-line note; Enter saves (an empty note is fine), Esc discards. Null: discarded.</summary>
        private string? AskBugNote(BugCaptureData d)
        {
            using var f = new Form
            {
                Text = "Capture Bug", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(460, 130), KeyPreview = true,
            };
            var label = new Label
            {
                AutoSize = false, Location = new Point(12, 10), Size = new Size(436, 36),
                Text = $"Captured the last {d.Seconds:F0} s{(d.NoMovieWhy != null ? " (no movie: " + d.NoMovieWhy + ")" : "")}. A one-line note (optional):",
            };
            var box = new TextBox { Location = new Point(12, 50), Size = new Size(436, 23), MaxLength = 300 };
            var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(292, 90), Size = new Size(75, 27) };
            var discard = new Button { Text = "Discard", DialogResult = DialogResult.Cancel, Location = new Point(373, 90), Size = new Size(75, 27) };
            f.Controls.AddRange(new Control[] { label, box, save, discard });
            f.AcceptButton = save; f.CancelButton = discard;
            return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
        }
    }
}
