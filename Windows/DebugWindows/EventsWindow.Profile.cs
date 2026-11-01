using System;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using NesEmulator.Debugging.Timeflow;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// The event view on a kept profile (BrokenDev T3): a profiled frame's register writes by scanline and dot, its DMA runs and interrupt entries,
    /// with a frame scrubber. Nothing is attached or armed: the events come from the profile's records.
    /// </summary>
    public sealed partial class EventsWindow
    {
        private readonly ProfileView? profile;
        private readonly NumericUpDown profFrame = new() { Name = "profileEventsFrame", AccessibleName = "profileEventsFrame", Width = 100 };

        internal EventsWindow(ProfileView view)
        {
            profile = view;
            hub = () => null; game = () => null;
            var m = view.Meta;
            Text = $"BrokenNes Events: profile {m.Id} (frames {m.From}-{m.LastFrameStored})";
            Name = "EventsWindowProfile";
            ClientSize = new Size(720, 600);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            list.Name = "profileEvents"; list.AccessibleName = "profileEvents";
            info.Name = "profileEventsInfo"; info.AccessibleName = "profileEventsInfo";
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4, 2, 4, 0), WrapContents = false };
            bar.Controls.Add(new Label { Text = "Frame", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            bar.Controls.Add(profFrame);
            Controls.Add(list);
            Controls.Add(bar);
            Controls.Add(info);
            long first = view.Reader.FirstFrame, last = view.Reader.LastFrame;
            profFrame.Minimum = Math.Max(0, first); profFrame.Maximum = Math.Max(profFrame.Minimum, last); profFrame.Value = profFrame.Minimum;
            profFrame.ValueChanged += (_, _) => ShowProfileFrame();
            profFrame.TextChanged += (_, _) => { if (decimal.TryParse(profFrame.Text, out var v) && v != profFrame.Value && v >= profFrame.Minimum && v <= profFrame.Maximum) profFrame.Value = v; };
            Shown += (_, _) => ShowProfileFrame();
        }

        /// <summary>Shows a frame (the desktop and the smoke choose one).</summary>
        internal void ShowProfileFrame(long frame) { if (frame >= profFrame.Minimum && frame <= profFrame.Maximum) profFrame.Value = frame; ShowProfileFrame(); }

        private void ShowProfileFrame()
        {
            if (profile == null || IsDisposed) return;
            try
            {
                var fv = profile.Frame((long)profFrame.Value);
                var ev = fv.Events;
                list.BeginUpdate(); list.Items.Clear();
                if (ev["lines"] is JsonArray a) list.Items.AddRange(a.Select(x => (object)(x?.ToString() ?? "")).ToArray());
                list.EndUpdate();
                info.Text = ev["error"] is { } err ? err.ToString()
                          : $"{profile.Meta.Console}: frame {fv.Frame}: {ev["registerWrites"]} register writes, {ev["dmaRuns"]} DMA runs, {ev["interrupts"]} interrupts (profile {profile.Meta.Id})";
            }
            catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or ArgumentException) { info.Text = "profile: " + e.Message; }
        }
    }
}
