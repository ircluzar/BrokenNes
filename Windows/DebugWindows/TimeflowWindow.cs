using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// Emulator > Debug Windows > Timeflow (BrokenDev T1 / T2): the REC switch (the user's own: recording is off until it is ticked), what is being
    /// recorded and the space used, the running game's timelines (a branch under its parent), and a scrubber over the chosen one: Jump puts the
    /// game at that frame (paused, its state proved to be the recorded one), Resume plays on live (a branch while recording), Replay plays the
    /// recorded inputs (checked against the recording frame by frame). The window's timer lives only while it is open; nothing is saved.
    /// </summary>
    public sealed class TimeflowWindow : Form
    {
        private readonly MainForm form;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
        private readonly CheckBox rec = new() { Name = "timeflowRec", AccessibleName = "timeflowRec", Text = "REC: record this game (a state every frame)", AutoSize = true };
        private readonly Label status = new() { Name = "timeflowStatus", AutoSize = false, Dock = DockStyle.Top, Height = 40, Padding = new Padding(4, 2, 4, 2) };
        private readonly ListBox list = new() { Name = "timeflowTimelines", AccessibleName = "timeflowTimelines", Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Consolas", 9f) };
        private readonly TrackBar scrub = new() { Name = "timeflowScrubber", AccessibleName = "timeflowScrubber", Dock = DockStyle.Top, TickStyle = TickStyle.None, Minimum = 0, Maximum = 0 };
        private readonly NumericUpDown frame = new() { Name = "timeflowFrame", AccessibleName = "timeflowFrame", Width = 110, Minimum = 0, Maximum = 0 };
        private readonly Button jump = new() { Name = "timeflowJump", AccessibleName = "timeflowJump", Text = "Jump", Width = 70 };
        private readonly Button live = new() { Name = "timeflowResumeLive", AccessibleName = "timeflowResumeLive", Text = "Resume (live)", Width = 100 };
        private readonly Button replay = new() { Name = "timeflowReplay", AccessibleName = "timeflowReplay", Text = "Replay recorded", Width = 110 };
        private readonly Label result = new() { Name = "timeflowResult", AutoSize = false, Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(4, 2, 4, 2) };
        private readonly TextBox info = new() { Name = "timeflowFrameInfo", AccessibleName = "timeflowFrameInfo", Dock = DockStyle.Bottom, ReadOnly = true, Height = 22 };
        private List<TimelineInfo> shown = new();
        private string listKey = "";
        private bool syncing;

        public TimeflowWindow(MainForm form)
        {
            this.form = form;
            Text = "BrokenNes Timeflow";
            Name = "TimeflowWindow";
            ClientSize = new Size(760, 420);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false, Padding = new Padding(4, 4, 4, 0) };
            top.Controls.Add(rec);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false, Padding = new Padding(4, 2, 4, 0) };
            bar.Controls.Add(new Label { Text = "Frame", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            bar.Controls.Add(frame); bar.Controls.Add(jump); bar.Controls.Add(live); bar.Controls.Add(replay);
            Controls.Add(list);
            Controls.Add(info);
            Controls.Add(result);
            Controls.Add(bar);
            Controls.Add(scrub);
            Controls.Add(status);
            Controls.Add(top);
            rec.CheckedChanged += (_, _) => { if (!syncing) form.TimeflowSetRecording(rec.Checked); Refresh2(); };
            list.SelectedIndexChanged += (_, _) => Selected();
            scrub.Scroll += (_, _) => { syncing = true; frame.Value = Math.Clamp(scrub.Value, frame.Minimum, frame.Maximum); syncing = false; ShowFrame(); };
            frame.ValueChanged += (_, _) => { if (syncing) return; if (frame.Value >= scrub.Minimum && frame.Value <= scrub.Maximum) scrub.Value = (int)frame.Value; ShowFrame(); };
            jump.Click += async (_, _) =>
            {
                var t = Current; if (t == null) { result.Text = "choose a timeline"; return; }
                result.Text = "jumping...";
                result.Text = await form.TimeflowJumpAsync(t.Dir, (long)frame.Value);
            };
            live.Click += async (_, _) => result.Text = await form.TimeflowResumeAsync(false);
            replay.Click += async (_, _) => result.Text = await form.TimeflowResumeAsync(true);
            timer.Tick += (_, _) => Refresh2();
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
            timer.Start();
            Refresh2();
        }

        private TimelineInfo? Current => list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count ? shown[list.SelectedIndex] : null;

        private void Refresh2()
        {
            if (IsDisposed) return;
            syncing = true; rec.Checked = form.TimeflowRecording; syncing = false;
            var r = form.TimeflowCurrent;
            string g = form.TimeflowGameDir() ?? "";
            string line1 = form.TimeflowRecording
                ? (r != null ? $"REC: timeline {r.Id}, {r.FramesCaptured} frames, {r.StoredBytes / (1024.0 * 1024):0.0} MB written; the game's folder {r.GameBytes / (1024.0 * 1024):0.0} MB of {r.CapBytes / (1024.0 * 1024 * 1024):0.##} GB"
                             : form.TimeflowRefused.Length > 0 ? "REC is on but this game cannot be recorded: " + form.TimeflowRefused : "REC is on: the recording starts with the game's next frame")
                : "Recording is off (nothing is recorded or written until you tick REC).";
            string line2 = (form.TimeflowLastEnd.Length > 0 ? "last timeline " + form.TimeflowLastEnd + ". " : "") + form.TimeflowReplayState();
            status.Text = line1 + "\n" + line2 + (g.Length > 0 ? "" : "\n(no game)");
            // the timelines of the running game (refreshed when they change)
            List<TimelineInfo> all = g.Length > 0 ? TimeflowStore.List(g) : new();
            string key = string.Join("|", all.Select(t => $"{t.Meta.Id}:{t.Meta.Frames}:{t.Meta.Status}"));
            if (key == listKey) return;
            listKey = key;
            string? sel = Current?.Meta.Id;
            var tree = TimeflowPlayback.Tree(all).ToList();
            shown = tree.Select(x => x.T).ToList();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var (t, d) in tree)
                list.Items.Add($"{new string(' ', d * 2)}{t.Meta.Id}  frames {t.Meta.StartFrame}-{t.Meta.StartFrame + t.Meta.Frames - 1}  {t.Meta.Status}{(t.Meta.Keep ? " KEEP" : "")}  {t.Bytes / 1024} KB  {t.Meta.Source}{(t.Meta.ParentId != null ? $"  (branch of {t.Meta.ParentId} at {t.Meta.ParentFrame})" : "")}");
            list.EndUpdate();
            int i = sel != null ? shown.FindIndex(t => t.Meta.Id == sel) : -1;
            if (i < 0 && shown.Count > 0) i = shown.Count - 1;
            if (i >= 0) list.SelectedIndex = i;
        }

        private void Selected()
        {
            var t = Current;
            if (t == null) return;
            long start = t.Meta.StartFrame, end = Math.Max(start, t.Meta.StartFrame + t.Meta.Frames - 1);
            syncing = true;
            frame.Minimum = 0; frame.Maximum = end; frame.Minimum = start;
            scrub.Minimum = (int)Math.Min(start, int.MaxValue); scrub.Maximum = (int)Math.Min(end, int.MaxValue);
            if (frame.Value < start || frame.Value > end) frame.Value = start;
            scrub.Value = (int)frame.Value;
            syncing = false;
            ShowFrame();
        }

        private void ShowFrame()
        {
            var t = Current;
            if (t == null || t.Meta.Frames == 0) { info.Text = ""; return; }
            try
            {
                using var rd = new TimelineReader(t.Dir);
                long f = (long)frame.Value;
                if (!rd.Has(f)) { info.Text = $"frame {f} is not written yet"; return; }
                var row = rd.Row(f); var ex = rd.Extras(f);
                string changed = string.Join(", ", t.Meta.Domains.Select((d, i) => i < ex.Domains.Count && ex.Domains[i].ChangedBytes > 0 ? $"{d.Id} {ex.Domains[i].ChangedBytes}" : null).Where(x => x != null));
                info.Text = $"frame {f}: pads {(PadButtons)row.P1}{(row.IsKey ? ", keyframe" : "")}; picture {row.Picture:x16}; changed bytes: {(changed.Length > 0 ? changed : "none")}" +
                            (t.Meta.Vars.Count > 0 ? "; " + string.Join(", ", t.Meta.Vars.Select((v, i) => $"{v.Split('=')[0]}={(i < ex.Vars.Length ? ex.Vars[i] : 0)}")) : "");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentOutOfRangeException) { info.Text = e.Message; }
        }
    }
}
