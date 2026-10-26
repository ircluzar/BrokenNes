using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BrokenNes.Windows.WebApi;
using NesEmulator.Debugging;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// The sound monitor (BrokenDev D8, "D8 settled"): one line per channel of the running console's sound chip and a level bar each
    /// (<see cref="DebugSound.Channels"/>). Read only, nothing attached or armed; the timer lives only while the window is open. Muting the
    /// NES's channels stays in Sound > Sound Channels.
    /// </summary>
    public sealed class SoundWindow : Form
    {
        private readonly Func<DebuggerHub?> hub;
        private readonly Func<object?> game;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
        private readonly ListBox list = new() { Name = "channels", AccessibleName = "channels", Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Consolas", 9f) };
        private readonly Panel bars = new() { Name = "levels", Dock = DockStyle.Left, Width = 120 };
        private readonly TextBox info = new() { Name = "info", AccessibleName = "info", Dock = DockStyle.Bottom, ReadOnly = true };
        private List<DebugSound.Channel> channels = new();
        private string lastKey = "";

        public SoundWindow(Func<DebuggerHub?> hub, Func<object?> game)
        {
            this.hub = hub; this.game = game;
            Text = "BrokenNes Sound";
            Name = "SoundWindow";
            ClientSize = new Size(860, 260);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(bars, true);
            bars.Paint += PaintBars;
            Controls.Add(list);
            Controls.Add(bars);
            Controls.Add(info);
            timer.Tick += (_, _) => RefreshView();
            Shown += (_, _) => { RefreshView(); timer.Start(); };
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
        }

        private void RefreshView()
        {
            if (IsDisposed) return;
            var h = hub(); string? why = null;
            var g = game();
            var m = h?.Machine(g, out why);
            if (m == null) { info.Text = h == null ? "the app's debugger hub is not up yet" : "no game: " + why; return; }
            channels = DebugSound.Channels(m, g, out string what);
            var lines = channels.Select(c => c.ToString()).ToArray();
            string key = string.Join("\n", lines);
            if (key != lastKey)
            {
                lastKey = key;
                list.BeginUpdate(); list.Items.Clear(); list.Items.AddRange(lines.Cast<object>().ToArray()); list.EndUpdate();
            }
            string it = $"{m.Console}: {what}";
            if (info.Text != it) info.Text = it;
            bars.Invalidate();
        }

        private void PaintBars(object? sender, PaintEventArgs e)
        {
            e.Graphics.Clear(SystemColors.Control);
            int rowH = list.ItemHeight > 0 ? list.ItemHeight : 15;
            for (int i = 0; i < channels.Count; i++)
            {
                var c = channels[i];
                var r = new Rectangle(4, 2 + i * rowH, bars.Width - 8, rowH - 3);
                e.Graphics.DrawRectangle(SystemPens.ControlDark, r);
                if (c.Level > 0)
                    using (var b = new SolidBrush(c.On ? Color.SeaGreen : Color.Gray))
                        e.Graphics.FillRectangle(b, r.X + 1, r.Y + 1, (int)((r.Width - 1) * Math.Min(1, c.Level)), r.Height - 1);
                else if (c.Level < 0 && c.On)
                    using (var b = new SolidBrush(Color.SeaGreen)) e.Graphics.FillRectangle(b, r.X + 1, r.Y + 1, 6, r.Height - 1);
            }
        }
    }
}
