using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using BrokenNes.Windows.AutoTas;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// The AutoTAS activity (docs/projects/autotas-plan.md section 5). When a game loads, its ROM's sha1/sha256 is looked up in the AutoTAS packages
    /// (<see cref="AutoTasIndex"/>); only when one is there does "AutoTAS" show in Tools &amp; Activities (and an "AutoTAS" mark in the menu bar).
    /// The activity is a side panel snapped to the main window's right edge: the chapters (thumbnail, title, kind, length), Jump (the chapter's
    /// clean state by default, its raw milestone state as an option), Play TAS from here / Pause, speed, Take control, and Return (back to the
    /// state from before the last jump or take-over). A jump loads the package's state with the app's own save-state loading; playing hands the
    /// chapter segments' recorded pads (.bnm) to the game, one chapter after another, until the last one ends or the player takes control.
    /// </summary>
    public partial class MainForm
    {
        private AutoTasPackage? atPackage;
        private string atNote = "";
        private ToolStripLabel? atBadge;
        // playback (emulation thread reads; the UI thread starts and stops it under atLock)
        private readonly object atLock = new();
        private Movie? atSeg; private int atSegChapter = -1; private long atSegFrame; private bool atSegClean;
        private bool atPlaying;
        private int atCurrent = -1;          // the chapter the game is in (from a jump or playback)
        private bool atCurrentClean = true;
        private byte[]? atReturnState;       // the game before the last jump / take-over
        private string atReturnWhat = "";
        private string atLast = "";
        private float atSpeed = 1f;

        internal AutoTasPackage? AutoTasPackageNow => atPackage;
        internal int AutoTasCurrent => atCurrent;
        internal bool AutoTasPlaying => atPlaying;
        internal bool AutoTasCanReturn => atReturnState != null;
        internal string AutoTasLast => atLast;
        internal float AutoTasSpeed => atSpeed;

        // ------------------------------------------------------------------------------------------------ discovery

        /// <summary>A game was loaded (from BugGameLoaded): look its package up off the UI thread, then offer the activity or take it away.</summary>
        private void AtGameLoaded(byte[] rom, string romName)
        {
            lock (atLock) { atSeg = null; atPlaying = false; atCurrent = -1; atReturnState = null; }
            atPackage = null; atNote = "";
            if (string.Equals(romName, "test.nes", StringComparison.OrdinalIgnoreCase)) { AtRefreshMenus(); return; }
            var copy = rom;
            Task.Run(() =>
            {
                var (s1, s256) = AutoTasIndex.Hashes(copy);
                var p = AutoTasIndex.Find(s1, s256, out string note);
                if (!ReferenceEquals(bugRom, copy)) return;   // another game was loaded meanwhile
                atPackage = p; atNote = p == null ? note : "";
                if (p != null) Console.WriteLine($"[AutoTAS] {p.Manifest.GameId} v{p.Manifest.Version}: {p.Manifest.Chapters.Count} chapters ({p.Dir})");
                AtRefreshMenus();
            });
        }

        private void AtRefreshMenus()
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    RebuildToolsMenu();
                    if (atBadge != null) atBadge.Visible = atPackage != null;
                    if (atPackage == null && debugWindows.TryGetValue("autotas", out var w) && !w.IsDisposed) w.Close();
                    else if (debugWindows.TryGetValue("autotas", out var w2) && w2 is DebugWindows.AutoTasPanel panel) panel.Reload();
                    if (atPackage == null && debugWindows.TryGetValue("autotasvars", out var v) && !v.IsDisposed) v.Close();
                    else if (debugWindows.TryGetValue("autotasvars", out var v2) && v2 is DebugWindows.AutoTasVariablesPanel vars) vars.Reload();
                }));
            }
            catch (InvalidOperationException) { }
        }

        /// <summary>Tools &amp; Activities: the AutoTAS item (only when the running game has a package). Called at the end of RebuildToolsMenu.</summary>
        private void AtAddMenuItem(ToolStripMenuItem menu)
        {
            if (atPackage == null) return;
            var item = new ToolStripMenuItem($"AutoTAS: {AtGameTitle()}", null, (_, _) => OpenAutoTasPanel()) { Name = "autotasMenu", Tag = "AutoTAS", Font = new Font(menu.Font, FontStyle.Bold) };
            menu.DropDownItems.Insert(0, item);
            if (menu.DropDownItems.Count > 1) menu.DropDownItems.Insert(1, new ToolStripSeparator { Tag = "AutoTAS" });
            if (atBadge == null && MainMenuStrip != null)
            {
                atBadge = new ToolStripLabel("AutoTAS") { Name = "autotasBadge", Alignment = ToolStripItemAlignment.Right, ForeColor = Color.FromArgb(40, 140, 60), Font = new Font(menu.Font, FontStyle.Bold), IsLink = true, ToolTipText = "this game has an AutoTAS: open the chapter panel" };
                atBadge.Click += (_, _) => OpenAutoTasPanel();
                MainMenuStrip.Items.Add(atBadge);
            }
            if (atBadge != null) atBadge.Visible = true;
        }

        internal string AtGameTitle() => atPackage == null ? "" : !string.IsNullOrWhiteSpace(atPackage.Manifest.Title) ? atPackage.Manifest.Title! : atPackage.Manifest.GameId;

        /// <summary>Whether the running cores are the package's (green) or a state may not fit ("replay needed").</summary>
        internal (bool Match, string Text) AutoTasCoreMatch()
        {
            var c = atPackage?.Manifest.Core;
            if (c == null) return (true, "no core pinned");
            string now = nes != null ? NesEmulator.Debugging.Capture.BugCaptureSource.NesCores(nes) : session != null ? SessionCores(session.Console) : "";
            // "FIX/FIX/TUXN" (CPU/PPU/APU, maybe " ntsc" after), slot by slot against the package's CPU_FIX / PPU_FIX / APU_FIX
            static string Bare(string x) => x.Replace("CPU_", "").Replace("PPU_", "").Replace("APU_", "");
            var have = now.Split(' ')[0].Split('/');
            var want = new[] { c.Cpu, c.Ppu, c.Apu };
            bool ok = true;
            for (int i = 0; i < want.Length; i++)
                if (!string.IsNullOrWhiteSpace(want[i]) && (i >= have.Length || !string.Equals(Bare(have[i]), Bare(want[i]!), StringComparison.OrdinalIgnoreCase))) ok = false;
            return (ok, ok ? $"cores match ({now})" : $"replay needed: made on {string.Join("/", want.Select(x => Bare(x ?? "?")))}, running {now} (jumps still load; playback may drift)");
        }

        // ------------------------------------------------------------------------------------------------ the frame loop (emulation thread)

        /// <summary>Before a NES frame: while the TAS plays, the game gets the segment's pads (replacing the player's).</summary>
        private void AtPads(bool[] p1, bool[] p2)
        {
            if (!atPlaying) return;
            var pads = AtNextPads();
            if (pads == null) return;
            Array.Copy(ToNesOrder(pads.Value.P1), p1, 8); Array.Copy(ToNesOrder(pads.Value.P2), p2, 8);
        }

        private void AtPads(ref PadButtons p1, ref PadButtons p2)
        {
            if (!atPlaying) return;
            var pads = AtNextPads();
            if (pads != null) (p1, p2) = pads.Value;
        }

        private (PadButtons P1, PadButtons P2)? AtNextPads()
        {
            lock (atLock)
            {
                if (!atPlaying || atPackage == null) return null;
                while (atSeg == null || atSegFrame >= atSeg.Frames)
                {
                    int next = atSeg == null ? atSegChapter : atSegChapter + 1;
                    if (next >= atPackage.Manifest.Chapters.Count) { atPlaying = false; atSeg = null; atLast = "the TAS ended (the last chapter is done): you have the pad"; AtSpeedNow(1f, false); return null; }
                    // the clean segment fits only the clean state it was recorded from: after the first chapter, the raw run goes on
                    string? path = atPackage.SegOf(atPackage.Manifest.Chapters[next], atSeg == null && atSegClean);
                    if (path == null) { atPlaying = false; atSeg = null; atLast = $"chapter {atPackage.Manifest.Chapters[next].Id} has no segment: you have the pad"; return null; }
                    try { atSeg = Movie.Load(path); }
                    catch (Exception e) when (e is IOException or FormatException) { atPlaying = false; atSeg = null; atLast = "the segment would not load: " + e.Message; return null; }
                    atSegChapter = next; atSegFrame = 0; atCurrent = next;
                }
                return atSeg.PadsAt(atSegFrame++);
            }
        }

        // ------------------------------------------------------------------------------------------------ the panel's controls (UI thread)

        private byte[] AtSaveNow()
        {
            if (nes != null) return Encoding.UTF8.GetBytes(nes.SaveState());
            if (session != null) return session.SaveState();
            throw new InvalidOperationException("no game is running");
        }

        private void AtLoadNow(byte[] state)
        {
            if (nes != null) nes.LoadState(Encoding.UTF8.GetString(state));
            else if (session != null) session.LoadState(state);
            else throw new InvalidOperationException("no game is running");
        }

        /// <summary>Puts the game at a chapter's start (its clean state, or its raw milestone state); playing stops; Return can undo it.</summary>
        internal async Task<string> AutoTasJumpAsync(int chapter, bool clean)
        {
            var p = atPackage;
            if (p == null) return atLast = "no AutoTAS for this game";
            if (chapter < 0 || chapter >= p.Manifest.Chapters.Count) return atLast = "no such chapter";
            var ch = p.Manifest.Chapters[chapter];
            string? path = p.StateOf(ch, clean);
            if (path == null && clean) { path = p.StateOf(ch, false); clean = false; }
            if (path == null) return atLast = $"chapter {ch.Id} has no state (a replay from full.bnm is not built yet)";
            byte[] state;
            try { state = ReadAutoTasStateFile(path); } catch (Exception e) when (e is IOException or InvalidDataException) { return atLast = e.Message; }
            bool wasPaused = isPaused;
            try
            {
                string r = await RunOnEmulationThreadAsync(() =>
                {
                    lock (emulationLock)
                    {
                        lock (atLock) { atPlaying = false; atSeg = null; }
                        var before = AtSaveNow();
                        TfEndReplay("an AutoTAS jump");
                        BugBreak($"AutoTAS jumped to {ch.Id}");
                        AtLoadNow(state);
                        atReturnState = before; atReturnWhat = "before the jump to " + ch.Title;
                        atCurrent = chapter; atCurrentClean = clean;
                        return $"at {ch.Title} ({(clean ? "clean" : "raw")} state){(wasPaused ? " (paused)" : "")}";
                    }
                });
                AtSpeedNow(1f, false);
                return atLast = r;
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException) { return atLast = "the state would not load: " + e.Message; }
        }

        /// <summary>Plays the TAS from the start of the current chapter's segment (after a jump), or goes on playing where it was paused.</summary>
        internal string AutoTasPlay()
        {
            if (atPackage == null) return atLast = "no AutoTAS for this game";
            lock (atLock)
            {
                if (atSeg == null)
                {
                    if (atCurrent < 0) return atLast = "jump to a chapter first: playing starts at a chapter's state";
                    atSegChapter = atCurrent; atSegClean = atCurrentClean; atSegFrame = 0;
                }
                atPlaying = true;
            }
            isPaused = false;
            AtSpeedNow(atSpeed, true);
            return atLast = $"playing the TAS from {atPackage.Manifest.Chapters[Math.Max(0, atCurrent)].Title}";
        }

        internal string AutoTasPause() { isPaused = true; return atLast = atPlaying ? "paused (Play goes on)" : "paused"; }

        /// <summary>Stops the TAS where it is and hands the pad to the player; Return goes back to the state from before.</summary>
        internal async Task<string> AutoTasTakeControlAsync()
        {
            try
            {
                await RunOnEmulationThreadAsync(() => { lock (emulationLock) { if (atPlaying) { atReturnState = AtSaveNow(); atReturnWhat = "where you took control"; } lock (atLock) { atPlaying = false; atSeg = null; } return 0; } });
            }
            catch (InvalidOperationException e) { return atLast = e.Message; }
            AtSpeedNow(1f, false);
            isPaused = false;
            return atLast = "you have the pad";
        }

        /// <summary>Back to the state from before the last jump or take-over.</summary>
        internal async Task<string> AutoTasReturnAsync()
        {
            var s = atReturnState;
            if (s == null) return atLast = "nothing to return to";
            try
            {
                await RunOnEmulationThreadAsync(() => { lock (emulationLock) { lock (atLock) { atPlaying = false; atSeg = null; } BugBreak("AutoTAS returned"); AtLoadNow(s); return 0; } });
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or NotSupportedException) { return atLast = "could not return: " + e.Message; }
            string what = atReturnWhat;
            atReturnState = null; atCurrent = -1;
            AtSpeedNow(1f, false);
            return atLast = "back " + what;
        }

        internal string AutoTasSetSpeed(float speed)
        {
            atSpeed = speed <= 0 ? 1f : speed;
            if (atPlaying) AtSpeedNow(atSpeed, true);
            return atLast = $"TAS speed {(atSpeed >= 16 ? "max" : atSpeed + "x")}";
        }

        /// <summary>The TAS's speed rides on the app's speed override (the Speed Control window's); 1x hands it back.</summary>
        private void AtSpeedNow(float speed, bool playing)
        {
            if (speedControlForm != null && !speedControlForm.IsDisposed) return;   // the user's own speed control wins
            if (playing && speed != 1f) { speedOverride = speed; hasSpeedOverride = true; }
            else { speedOverride = 1f; hasSpeedOverride = false; }
            resetTimingAccumulator = true;
            try { audioManager?.SetSpeedMultiplier(hasSpeedOverride ? speedOverride : 1f, preserveBuffer: true); } catch { }
        }

        // ------------------------------------------------------------------------------------------------ the panel, the API

        internal void OpenAutoTasPanel()
        {
            if (atPackage == null) return;
            if (debugWindows.TryGetValue("autotas", out var open) && !open.IsDisposed) { open.Activate(); return; }
            var f = new DebugWindows.AutoTasPanel(this);
            f.FormClosed += (_, _) => { debugWindows.Remove("autotas"); if (debugWindows.TryGetValue("autotasvars", out var vw) && !vw.IsDisposed) vw.Close(); };
            debugWindows["autotas"] = f;
            f.Show(this);
            OpenAutoTasVariables();
            Console.WriteLine("[AutoTAS] panel opened");
        }

        /// <summary>The left-hand variables stripe (the game dossier's RAM variables, live); opens with the chapter panel.</summary>
        internal void OpenAutoTasVariables()
        {
            if (atPackage == null) return;
            if (debugWindows.TryGetValue("autotasvars", out var open) && !open.IsDisposed) { open.Activate(); return; }
            var v = new DebugWindows.AutoTasVariablesPanel(this);
            v.FormClosed += (_, _) => debugWindows.Remove("autotasvars");
            debugWindows["autotasvars"] = v;
            v.Show(this);
        }

        /// <summary>
        /// A side-effect-free byte reader for the variables stripe: NES system RAM ($0000-$1FFF, mirrored) and cartridge RAM ($6000-$7FFF);
        /// anything else reads -1. Null when the game is not a NES game (the other consoles come later).
        /// </summary>
        internal Func<int, int>? AutoTasPeeker()
        {
            var n = nes;
            if (n == null) return null;
            return a => a is >= 0 and < 0x2000 ? n.PeekSystemRam(a & 0x7FF) : a is >= 0x6000 and < 0x8000 ? n.PeekPrgRam(a - 0x6000) : -1;
        }

        /// <summary>The variables stripe as a PNG (null when closed).</summary>
        internal byte[]? AutoTasVariablesPng()
        {
            if (!debugWindows.TryGetValue("autotasvars", out var w) || w.IsDisposed) return null;
            using var bmp = new Bitmap(w.Width, w.Height);
            w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        internal object AutoTasStatus()
        {
            var p = atPackage;
            var (match, coreText) = p != null ? AutoTasCoreMatch() : (false, "");
            return new
            {
                available = p != null, note = atNote.Length > 0 ? atNote : null, root = AutoTasIndex.Root(),
                game = p?.Manifest.GameId, title = p != null ? AtGameTitle() : null, version = p?.Manifest.Version, dir = p?.Dir,
                coreMatch = p != null ? match : (bool?)null, core = p != null ? coreText : null,
                current = atCurrent, currentClean = atCurrentClean, playing = atPlaying, paused = isPaused, segFrame = atSeg != null ? atSegFrame : (long?)null,
                canReturn = atReturnState != null, speed = atSpeed, last = atLast,
                panelOpen = debugWindows.TryGetValue("autotas", out var w) && !w.IsDisposed,
                variables = debugWindows.TryGetValue("autotasvars", out var vw) && vw is DebugWindows.AutoTasVariablesPanel vp && !vp.IsDisposed ? vp.Snapshot() : null,
                variablesCollapsed = debugWindows.TryGetValue("autotasvars", out var vw2) && vw2 is DebugWindows.AutoTasVariablesPanel vp2 && !vp2.IsDisposed ? vp2.Collapsed : (bool?)null,
                chapters = p?.Manifest.Chapters.Select((c, i) => (object)new { index = i, id = c.Id, title = c.Title, kind = c.Kind, frames = c.Frames, score = c.Quality?.Score, deaths = c.Quality?.Deaths, hasClean = p.StateOf(c, true) != null, hasRaw = p.StateOf(c, false) != null, hasSeg = p.SegOf(c, false) != null, thumb = p.ThumbOf(c) != null }).ToArray(),
            };
        }

        /// <summary>A picture of the panel (PNG) for tests and agents; null when it is closed.</summary>
        internal byte[]? AutoTasPanelPng()
        {
            if (!debugWindows.TryGetValue("autotas", out var w) || w.IsDisposed) return null;
            using var bmp = new Bitmap(w.Width, w.Height);
            w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        /// <summary>
        /// For the tools lane building a package on a desktop app: writes the running game's state (the bytes a jump loads) to a file under the
        /// AutoTAS root (nowhere else). Returns the path written.
        /// </summary>
        internal async Task<string> AutoTasCaptureStateAsync(string rel)
        {
            string root = Path.GetFullPath(AutoTasIndex.Root()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, rel ?? ""));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("the path must be under the AutoTAS root " + root);
            byte[] s = await RunOnEmulationThreadAsync(() => { lock (emulationLock) return AtSaveNow(); });
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            new NesEmulator.Debugging.Movies.AutoTasState { Console = nes != null ? "nes" : "other", Cores = "FIX/FIX/FIX", State = s }.Save(full);
            return full;
        }

        /// <summary>Reads a package state: the tools' "bnstate 1" file (header + gzip), or a bare save-state file (older samples).</summary>
        private static byte[] ReadAutoTasStateFile(string path)
        {
            var head = new byte[9];
            int n;
            using (var f = File.OpenRead(path)) n = f.Read(head, 0, 9);
            return n == 9 && Encoding.ASCII.GetString(head) == NesEmulator.Debugging.Movies.AutoTasState.Magic
                ? NesEmulator.Debugging.Movies.AutoTasState.Load(path).State
                : File.ReadAllBytes(path);
        }

        private T AtUi<T>(Func<T> f) => InvokeRequired ? (T)Invoke(f) : f();

        private void WireAutoTasApi(WebApi.WebApiServer server)
        {
            server.GetAutoTasStatus = () => AtUi(AutoTasStatus);
            server.AutoTasOpenPanel = () => AtUi(() => { OpenAutoTasPanel(); return AutoTasStatus(); });
            server.AutoTasPanelPng = () => AtUi(AutoTasPanelPng);
            server.AutoTasVariablesPng = () => AtUi(AutoTasVariablesPng);
            server.AutoTasVariablesCollapse = (c, all) => AtUi(() =>
            {
                OpenAutoTasVariables();
                if (c is bool b && debugWindows.TryGetValue("autotasvars", out var w) && w is DebugWindows.AutoTasVariablesPanel vp) vp.SetCollapsed(b);
                if (all is bool a && debugWindows.TryGetValue("autotasvars", out var w3) && w3 is DebugWindows.AutoTasVariablesPanel vp3) vp3.SetShowAll(a);
                return AutoTasStatus();
            });
            server.AutoTasCaptureState = rel => AutoTasCaptureStateAsync(rel);
            server.AutoTasCommand = async (cmd, chapter, clean, speed) =>
            {
                Task<string> t = AtUi(() => cmd switch
                {
                    "jump" => AutoTasJumpAsync(chapter ?? 0, clean ?? true),
                    "play" => Task.FromResult(AutoTasPlay()),
                    "pause" => Task.FromResult(AutoTasPause()),
                    "takeover" => AutoTasTakeControlAsync(),
                    "return" => AutoTasReturnAsync(),
                    "speed" => Task.FromResult(AutoTasSetSpeed(speed ?? 1f)),
                    _ => Task.FromResult("unknown command (jump, play, pause, takeover, return, speed)"),
                });
                string msg = await t;
                AtUi(() => { if (debugWindows.TryGetValue("autotas", out var w) && w is DebugWindows.AutoTasPanel pn) pn.RefreshState(); return 0; });
                return msg;
            };
        }
    }
}
