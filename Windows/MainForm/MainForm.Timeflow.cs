using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NesEmulator;
using NesEmulator.Debugging.Capture;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// BrokenDev Timeflow in the desktop app (milestones T1 and T2 of docs/projects/brokendev-timeflow-plan.md). THE CONSENT RULE: recording is
    /// OFF until the user switches it on (Emulator > Debug Windows > Timeflow: Record, or the Timeflow window's REC box); while it is on a red
    /// REC mark with the space used shows in the menu bar and the title. An agent can only ask (POST /api/timeflow/request): the app shows a prompt
    /// under the menu that does not stop the game, and nothing records unless the user presses Allow. Nothing is saved in the config (the switch is
    /// off at every start). Off, the frame loop's calls return at their first test and nothing is written. Timelines go to
    /// BROKENDEV_HOME\timeflow (never %APPDATA%\BrokenNes).
    /// </summary>
    public partial class MainForm
    {
        private readonly TimeflowSwitch tfSwitch = new();
        private long tfFrames;                     // frames since the running game was loaded (a timeline's frame numbers)
        private string tfOrigin = "you switched it on";
        // a jump: the next timeline is a branch of this one, from this frame
        private string? tfBranchDir; private long tfBranchFrame, tfBranchAt;
        // a replay of the recorded inputs after a jump (T2)
        private TimeflowPlayback? tfReplay; private long tfReplayFrame, tfReplayEnd, tfReplayStart; private long? tfReplayOff; private string tfReplayWhat = "", tfReplayResult = "";
        // an agent's request and its answer
        private sealed class TfRequest { public string Reason = "", From = "", State = "pending"; public DateTime At = DateTime.UtcNow; public DateTime? Answered; }
        private TfRequest? tfRequest;
        private Panel? tfPrompt;
        private ToolStripLabel? tfMark;
        private ToolStripMenuItem? tfMenuItem;
        private System.Windows.Forms.Timer? tfTimer;

        private bool TfActive => tfSwitch.On || tfSwitch.Current != null;

        // ------------------------------------------------------------------------------------------------ the frame loop (emulation thread)

        /// <summary>A game was loaded: frames count from its power-on again.</summary>
        private void TfGameLoaded() { tfFrames = 0; tfBranchDir = null; TfEndReplay("another game was loaded"); }

        /// <summary>Before the NES gets its pads: a replay hands it the recorded ones.</summary>
        private void TfReplayPads(bool[] p1, bool[] p2)
        {
            if (tfReplay == null) return;
            var (a, b) = tfReplay.Reader.Has(tfReplayFrame) ? tfReplay.Reader.Pads(tfReplayFrame) : (PadButtons.None, PadButtons.None);
            var n1 = ToNesOrder(a); var n2 = ToNesOrder(b);
            Array.Copy(n1, p1, 8); Array.Copy(n2, p2, 8);
        }

        private static bool[] ToNesOrder(PadButtons b) => new[]
        {
            (b & PadButtons.A) != 0, (b & PadButtons.B) != 0, (b & PadButtons.Select) != 0, (b & PadButtons.Start) != 0,
            (b & PadButtons.Up) != 0, (b & PadButtons.Down) != 0, (b & PadButtons.Left) != 0, (b & PadButtons.Right) != 0,
        };

        /// <summary>A session's pads, replaced by the recorded ones while a replay runs.</summary>
        private void TfReplayPads(ref PadButtons p1, ref PadButtons p2)
        {
            if (tfReplay == null) return;
            (p1, p2) = tfReplay.Reader.Has(tfReplayFrame) ? tfReplay.Reader.Pads(tfReplayFrame) : (PadButtons.None, PadButtons.None);
        }

        private void TfBeforeNesFrame(NES n, bool[] p1, bool[] p2)
        {
            if (!TfActive) return;
            if (string.Equals(n.RomName, "test.nes", StringComparison.OrdinalIgnoreCase)) { if (tfSwitch.Current != null) tfSwitch.Break("the idle screen"); return; }   // the idle screen is never recorded
            tfSwitch.BeforeFrame(n, () => TimeflowSource.For(n, () => BugCaptureSource.NesCores(n) + (BugCaptureSource.NesNtsc(n) ? " ntsc" : "")),
                                 () => TfOptions(BugCaptureSource.NesCores(n), BugCaptureSource.NesNtsc(n)), BugCaptureRing.FromNes(p1), BugCaptureRing.FromNes(p2));
        }

        private void TfBeforeSessionFrame(IConsoleSession s, PadButtons p1, PadButtons p2)
        {
            if (!TfActive) return;
            var c = s.Console;
            tfSwitch.BeforeFrame(s, () => TimeflowSource.For(s, () => SessionCores(c)), () => TfOptions(SessionCores(c), false), p1, p2);
        }

        /// <summary>After any frame (emulation thread): the recording's half, the replay's check, the frame count.</summary>
        private void TfAfterFrame(object machine)
        {
            tfFrames++;
            if (TfActive) tfSwitch.AfterFrame();
            if (tfReplay == null) return;
            try
            {
                var row = tfReplay.Reader.Row(tfReplayFrame);
                var src = machine is NES n ? TimeflowSource.For(n) : TimeflowSource.For((IConsoleSession)machine);
                // the NES holds its last picture for the first frame after a load (a NES state has no picture): that frame's RAM is compared, its picture not
                bool held = machine is NES && tfReplayFrame == tfReplayStart && src.RamHash() == row.Ram;
                if (!held && tfReplayOff == null && (src.PictureHash() != row.Picture || src.RamHash() != row.Ram))
                { tfReplayOff = tfReplayFrame; tfReplayWhat = src.PictureHash() != row.Picture ? "the picture" : "the work RAM"; }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidDataException) { }
            tfReplayFrame++;
            if (tfReplayFrame >= tfReplayEnd) TfEndReplay(tfReplayOff == null ? "the recording's end" : "");
        }

        private void TfEndReplay(string why)
        {
            var r = tfReplay;
            if (r == null) return;
            tfReplay = null;
            tfReplayResult = tfReplayOff == null ? $"replayed frames to {tfReplayFrame - 1}: in sync with the recording ({why})" : $"replay off the recording at frame {tfReplayOff} ({tfReplayWhat})";
            Console.WriteLine("[Timeflow] " + tfReplayResult);
            try { r.Dispose(); } catch { }
        }

        private TimeflowOptions TfOptions(string cores, bool ntsc)
        {
            var o = new TimeflowOptions
            {
                Rom = bugRom ?? Array.Empty<byte>(), RomName = bugRomName, Cores = cores, Ntsc = ntsc, Source = "desktop", Note = $"the desktop app ({tfOrigin})",
                StartFrame = tfFrames, PowerOnStart = tfFrames == 0,
            };
            if (tfBranchDir != null)
            {
                try
                {
                    var pm = TimelineMeta.Load(tfBranchDir);
                    o.ParentId = pm.Id; o.ParentFrame = tfBranchFrame; o.StartFrame = tfBranchFrame + (tfFrames - tfBranchAt);
                    o.GameDir = TimeflowStore.GameDirOf(tfBranchDir); o.PowerOnStart = false;
                }
                catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { tfBranchDir = null; }
            }
            return o;
        }

        // ------------------------------------------------------------------------------------------------ the switch (UI thread)

        /// <summary>The user's switch (the menu, the window's REC box) or the user's Allow on an agent's request.</summary>
        internal void TimeflowSetRecording(bool on, string origin = "you switched it on")
        {
            if (on == tfSwitch.On) { TfRefreshMark(); return; }
            if (on) { tfOrigin = origin; tfSwitch.SwitchOn(); }
            else
            {
                tfSwitch.SwitchOff();
                // close the timeline now when no frame is coming (paused, or no game)
                _ = RunOnEmulationThreadAsync(() => { if (!tfSwitch.On) tfSwitch.CloseNow("closed: recording was switched off"); });
            }
            Console.WriteLine($"[Timeflow] recording {(on ? "on (" + origin + ")" : "off")}");
            TfRefreshMark();
        }

        private void AddTimeflowMenu(ToolStripMenuItem debugWindowsMenu)
        {
            debugWindowsMenu.DropDownItems.Add(new ToolStripSeparator());
            tfMenuItem = new ToolStripMenuItem("Timeflow: &Record (REC)") { Name = "timeflowRecord", CheckOnClick = true, Checked = false };
            tfMenuItem.ToolTipText = "Records a state every frame (keyframes + page deltas) into BROKENDEV_HOME\\timeflow, to rewind to any frame. Off at every start; agents can only ask.";
            tfMenuItem.CheckedChanged += (_, _) => { if (tfMenuItem.Checked != tfSwitch.On) TimeflowSetRecording(tfMenuItem.Checked); };
            debugWindowsMenu.DropDownItems.Add(tfMenuItem);
            debugWindowsMenu.DropDownItems.Add(new ToolStripMenuItem("Time&flow (timelines, scrubber, jump, resume)", null, (_, _) => OpenTimeflowWindow()) { Name = "debugWindowTimeflow" });
            debugWindowsMenu.DropDownOpening += (_, _) => { if (tfMenuItem != null) tfMenuItem.Checked = tfSwitch.On; };
            TimeflowProfileInit();   // T3: the start-up sweep of profiles a crashed app left, and the app's own deleted as it closes
        }

        /// <summary>The REC mark: a red label at the right of the menu bar, shown while recording is on.</summary>
        private void AddTimeflowMark(MenuStrip menuStrip)
        {
            tfMark = new ToolStripLabel("") { Name = "timeflowRecMark", Alignment = ToolStripItemAlignment.Right, ForeColor = Color.White, BackColor = Color.FromArgb(200, 0, 0), Visible = false, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            tfMark.ToolTipText = "Timeflow is recording this game (Emulator > Debug Windows > Timeflow: Record to stop)";
            menuStrip.Items.Add(tfMark);
            tfSwitch.Stopped += reason => { try { if (IsHandleCreated) BeginInvoke(new Action(() => { Console.WriteLine("[Timeflow] " + reason); TfRefreshMark(); })); } catch (InvalidOperationException) { } };
        }

        /// <summary>The REC mark (menu bar, title) and the switch's menu check, refreshed once a second while recording.</summary>
        private void TfRefreshMark()
        {
            if (IsDisposed) return;
            bool on = tfSwitch.On;
            if (tfMenuItem != null && tfMenuItem.Checked != on) tfMenuItem.Checked = on;
            if (tfMark != null)
            {
                var r = tfSwitch.Current;
                tfMark.Visible = on;
                tfMark.Text = on ? (r != null ? $"● REC  {Mb(r.GameBytes > 0 ? r.GameBytes : r.StoredBytes)} of {Gb(r.CapBytes)}" : tfSwitch.Refused.Length > 0 ? "● REC  (refused: see Timeflow)" : "● REC  (waiting for a game)") : "";
            }
            if (on && tfTimer == null) { tfTimer = new System.Windows.Forms.Timer { Interval = 1000 }; tfTimer.Tick += (_, _) => TfRefreshMark(); tfTimer.Start(); }
            if (!on && tfTimer != null) { tfTimer.Stop(); tfTimer.Dispose(); tfTimer = null; }
            UpdateConsoleTitle();
        }

        private string TimeflowTitleNote() => tfSwitch.On ? " [REC]" : "";

        private static string Mb(long b) => $"{b / (1024.0 * 1024):0.0} MB";
        private static string Gb(long b) => $"{b / (1024.0 * 1024 * 1024):0.##} GB";

        // ------------------------------------------------------------------------------------------------ an agent's request: a prompt that does not stop the game

        internal object TimeflowRequest(string reason, string from)
        {
            if (tfSwitch.On) return new { success = true, request = "not needed", recording = true, note = "recording is already on (the user switched it on)" };
            if (tfRequest is { State: "pending" }) return new { success = true, request = "pending", note = "a request is already waiting for the user's answer", reason = tfRequest.Reason, from = tfRequest.From };
            var req = new TfRequest { Reason = reason.Length > 300 ? reason[..300] : reason, From = from.Length > 120 ? from[..120] : from };
            tfRequest = req;
            try { BeginInvoke(new Action(() => ShowTimeflowPrompt(req))); } catch (InvalidOperationException) { }
            Console.WriteLine($"[Timeflow] {req.From} asks to record: {req.Reason}");
            return new { success = true, request = "pending", recording = false, note = "the app is asking the user; nothing records unless the user allows it" };
        }

        internal object TimeflowCancelRequest()
        {
            var r = tfRequest;
            if (r is { State: "pending" }) { r.State = "cancelled"; r.Answered = DateTime.UtcNow; try { BeginInvoke(new Action(HideTimeflowPrompt)); } catch (InvalidOperationException) { } }
            return new { success = true, request = r?.State ?? "none" };
        }

        private void ShowTimeflowPrompt(TfRequest req)
        {
            HideTimeflowPrompt();
            var p = new Panel { Name = "timeflowPrompt", AccessibleName = "timeflowPrompt", Height = 36, BackColor = Color.FromArgb(255, 244, 200), Dock = DockStyle.Top };
            var text = new Label
            {
                Name = "timeflowPromptText", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0),
                Text = $"{req.From} asks to record your play with Timeflow (a state every frame, to rewind; the REC mark shows while it records){(req.Reason.Length > 0 ? ": " + req.Reason : "")}",
            };
            var allow = new Button { Name = "timeflowAllow", AccessibleName = "timeflowAllow", Text = "Allow", Dock = DockStyle.Right, Width = 80 };
            var decline = new Button { Name = "timeflowDecline", AccessibleName = "timeflowDecline", Text = "Decline", Dock = DockStyle.Right, Width = 80 };
            allow.Click += (_, _) => AnswerTimeflowRequest(req, true);
            decline.Click += (_, _) => AnswerTimeflowRequest(req, false);
            p.Controls.Add(text); p.Controls.Add(allow); p.Controls.Add(decline);
            tfPrompt = p;
            Controls.Add(p);
            p.BringToFront();
            // the prompt sits under the menu bar, over the top of the game (the game keeps running underneath)
            p.Dock = DockStyle.None;
            p.SetBounds(0, MainMenuStrip?.Height ?? 24, ClientSize.Width, 36);
            p.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        }

        private void HideTimeflowPrompt()
        {
            if (tfPrompt == null) return;
            Controls.Remove(tfPrompt); tfPrompt.Dispose(); tfPrompt = null;
        }

        private void AnswerTimeflowRequest(TfRequest req, bool allow)
        {
            if (req.State != "pending") { HideTimeflowPrompt(); return; }
            req.State = allow ? "allowed" : "declined"; req.Answered = DateTime.UtcNow;
            HideTimeflowPrompt();
            Console.WriteLine($"[Timeflow] the user {(allow ? "allowed" : "declined")} {req.From}'s request");
            if (allow) TimeflowSetRecording(true, $"you allowed {req.From}'s request");
        }

        // ------------------------------------------------------------------------------------------------ status, timelines (the API, the window)

        /// <summary>The running game's Timeflow folder (by its ROM's sha256), or null with no game.</summary>
        internal string? TimeflowGameDir()
        {
            var rom = bugRom;
            if (rom == null || (nes != null && session == null && string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase))) return null;
            try { return TimeflowStore.GameDir(TimeflowStore.Sha256(rom), bugRomName, out _); }
            catch (InvalidOperationException) { return null; }
        }

        internal object TimeflowStatus()
        {
            var r = tfSwitch.Current;
            var q = tfRequest;
            string? root = null; try { root = TimeflowStore.Root(); } catch (InvalidOperationException e) { root = "refused: " + e.Message; }
            return new
            {
                recording = tfSwitch.On, origin = tfSwitch.On ? tfOrigin : null,
                timeline = r?.Id, dir = r?.Dir, frames = r?.FramesCaptured, storedBytes = r?.StoredBytes, gameBytes = r?.GameBytes, capBytes = r?.CapBytes,
                stopReason = r?.StopReason, lastEnd = tfSwitch.LastEnd.Length > 0 ? tfSwitch.LastEnd : null, refused = tfSwitch.Refused.Length > 0 ? tfSwitch.Refused : null,
                finished = tfSwitch.Finished.Select(Path.GetFileName).ToArray(), root, gameDir = TimeflowGameDir(),
                request = q == null ? null : new { state = q.State, reason = q.Reason, from = q.From, at = q.At.ToString("o"), answered = q.Answered?.ToString("o") },
                replay = tfReplay != null ? new { timeline = tfReplay.Meta.Id, frame = tfReplayFrame, end = tfReplayEnd, offFrame = tfReplayOff } : null,
                lastReplay = tfReplayResult.Length > 0 ? tfReplayResult : null,
                rule = "recording is off until the user switches it on; an agent can only ask (POST /api/timeflow/request)",
            };
        }

        internal object TimeflowTimelines()
        {
            string? g = TimeflowGameDir();
            if (g == null) return new { gameDir = (string?)null, timelines = Array.Empty<object>() };
            return new
            {
                gameDir = g, usedBytes = TimeflowStore.DirBytes(g), capBytes = TimeflowStore.CapOf(g),
                timelines = TimeflowPlayback.Tree(TimeflowStore.List(g)).Select(x => (object)new { id = x.T.Meta.Id, depth = x.Depth, startFrame = x.T.Meta.StartFrame, frames = x.T.Meta.Frames, status = x.T.Meta.Status, parent = x.T.Meta.ParentId, parentFrame = x.T.Meta.ParentFrame, source = x.T.Meta.Source, bytes = x.T.Bytes, keep = x.T.Meta.Keep }).ToArray(),
            };
        }

        private void WireTimeflowApi(WebApi.WebApiServer server)
        {
            server.GetTimeflowStatus = TimeflowStatus;
            server.GetTimeflowTimelines = TimeflowTimelines;
            server.RequestTimeflow = (reason, from) => TimeflowRequest(reason, from);
            server.CancelTimeflowRequest = TimeflowCancelRequest;
            server.GetTimeflowProfile = TimeflowProfileStatus;
            server.GetTimeflowSearch = TimeflowSearchStatus;
        }

        // ------------------------------------------------------------------------------------------------ jump and resume (the window: the user's own controls)

        /// <summary>The source of the running game (emulation thread).</summary>
        private TimeflowSource? TfSourceNow()
        {
            if (session != null) { var c = session.Console; var s = session; return TimeflowSource.For(s, () => SessionCores(c)); }
            if (nes != null) { var n = nes; return TimeflowSource.For(n, () => BugCaptureSource.NesCores(n)); }
            return null;
        }

        /// <summary>
        /// Puts the running game at a frame of a timeline (the user's Jump): paused there, its state proved to be the recorded one. If recording
        /// is on, the frames played from there are a branch of that timeline. Returns a message for the window.
        /// </summary>
        internal async Task<string> TimeflowJumpAsync(string dir, long frame)
        {
            isPaused = true;
            try
            {
                return await RunOnEmulationThreadAsync(() =>
                {
                    lock (emulationLock)
                    {
                        var src = TfSourceNow() ?? throw new InvalidOperationException("no game is running");
                        TfEndReplay("a jump");
                        var pb = new TimeflowPlayback(dir);
                        try
                        {
                            pb.CheckMachine(src, TimeflowStore.Sha256(bugRom ?? Array.Empty<byte>()));
                            tfSwitch.Break($"you jumped to timeline {pb.Meta.Id} frame {frame}");
                            BugBreak($"Timeflow jumped to timeline {pb.Meta.Id} frame {frame}");
                            pb.Jump(src, frame);
                            tfBranchDir = pb.Dir; tfBranchFrame = frame; tfBranchAt = tfFrames;
                            return $"at frame {frame} of {pb.Meta.Id} (paused; its state is the recorded one)";
                        }
                        finally { pb.Dispose(); }
                    }
                });
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentOutOfRangeException or IOException) { return "jump refused: " + e.Message; }
        }

        /// <summary>After a jump: go on with live input (a branch when recording) or with the recorded inputs (checked against the recording).</summary>
        internal async Task<string> TimeflowResumeAsync(bool replay)
        {
            if (tfBranchDir == null) return "jump to a frame first";
            if (!replay) { TfEndReplay("you resumed live"); isPaused = false; return tfSwitch.On ? "playing live from the jumped frame: recorded as a branch" : "playing live from the jumped frame (not recorded: switch REC on to keep it)"; }
            string dir = tfBranchDir; long from = tfBranchFrame + (tfFrames - tfBranchAt);
            string msg = await RunOnEmulationThreadAsync(() =>
            {
                var pb = new TimeflowPlayback(dir);
                if (!pb.Reader.Has(from)) { pb.Dispose(); return "the timeline has no frame " + from; }
                tfReplay = pb; tfReplayFrame = from; tfReplayStart = from; tfReplayEnd = pb.Reader.EndFrame; tfReplayOff = null; tfReplayWhat = ""; tfReplayResult = "";
                return $"replaying the recorded inputs from frame {from} to {tfReplayEnd - 1}";
            });
            isPaused = false;
            return msg;
        }

        internal string TimeflowReplayState() => tfReplay != null ? $"replaying: frame {tfReplayFrame} of {tfReplayEnd - 1}{(tfReplayOff != null ? $", OFF the recording at {tfReplayOff} ({tfReplayWhat})" : ", in sync so far")}" : tfReplayResult;

        private void OpenTimeflowWindow()
        {
            if (debugWindows.TryGetValue("timeflow", out var open) && !open.IsDisposed) { open.Activate(); return; }
            var f = new DebugWindows.TimeflowWindow(this);
            f.Location = new Point(Right + 8 < Screen.FromControl(this).WorkingArea.Right - 200 ? Right + 8 : Left + 40, Top + 40);
            f.FormClosed += (_, _) => debugWindows.Remove("timeflow");
            debugWindows["timeflow"] = f;
            f.Show(this);
            Console.WriteLine("[debug windows] timeflow opened");
        }

        internal bool TimeflowRecording => tfSwitch.On;
        internal TimeflowRecorder? TimeflowCurrent => tfSwitch.Current;
        internal string TimeflowLastEnd => tfSwitch.LastEnd;
        internal string TimeflowRefused => tfSwitch.Refused;
        internal long TimeflowFrame => tfFrames;
    }
}
