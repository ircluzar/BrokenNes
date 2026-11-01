using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BrokenNes.Windows.WebApi;
using NesEmulator.Corruption;
using NesEmulator.Debugging;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// The palettes (BrokenDev D8, "D8 settled"): the NES's 32 bytes of palette RAM (PPU $3F00-$3F1F) through the NES colours; the SNES's
    /// CGRAM (256 BGR555 colours); the Game Boy's BGP / OBP0 / OBP1 and, on a Color, its background and sprite colour RAM. Swatches, and one
    /// line of values per palette (the list the tests read). Read through <see cref="DebuggerHub.Machine"/>'s memory domains: nothing attached,
    /// nothing armed; the timer lives only while the window is open.
    /// </summary>
    public sealed class PalettesWindow : Form
    {
        private readonly Func<DebuggerHub?> hub;
        private readonly Func<object?> game;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 150 };
        private readonly ListBox entries = new() { Name = "palettes", AccessibleName = "palettes", Dock = DockStyle.Bottom, Height = 170, IntegralHeight = false };
        private readonly Panel swatches = new() { Name = "swatches", Dock = DockStyle.Fill };
        private readonly Label status = new() { Name = "status", AccessibleName = "status", Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true };
        private List<(string label, Color[] colours)> rows = new();
        private string lastKey = "";

        public PalettesWindow(Func<DebuggerHub?> hub, Func<object?> game)
        {
            this.hub = hub; this.game = game;
            Text = "BrokenNes Palettes";
            Name = "PalettesWindow";
            ClientSize = new Size(600, 560);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            entries.Font = new Font("Consolas", 9f);
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(swatches, true);
            swatches.Paint += PaintSwatches;
            swatches.Resize += (_, _) => swatches.Invalidate();
            Controls.Add(swatches);
            Controls.Add(entries);
            Controls.Add(status);
            timer.Tick += (_, _) => RefreshView();
            Shown += (_, _) => { RefreshView(); timer.Start(); };
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
        }

        private static Color Rgb(int rgb) => Color.FromArgb(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF);
        internal static Color Bgr555(int c) { int r = c & 31, g = c >> 5 & 31, b = c >> 10 & 31; return Color.FromArgb(r << 3 | r >> 2, g << 3 | g >> 2, b << 3 | b >> 2); }
        private static readonly Color[] DmgGreys = { Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xAA, 0xAA, 0xAA), Color.FromArgb(0x55, 0x55, 0x55), Color.FromArgb(0, 0, 0) };

        private void RefreshView()
        {
            if (IsDisposed) return;
            var h = hub(); string? why = null;
            var m = h?.Machine(game(), out why);
            if (m == null) { status.Text = h == null ? "the app's debugger hub is not up yet" : "no game: " + why; return; }
            var lines = new List<string>();
            var rs = new List<(string, Color[])>();
            Read(m, lines, rs, out string what);
            string key = string.Join("\n", lines);
            if (key != lastKey)
            {
                lastKey = key; rows = rs;
                int sel = entries.SelectedIndex;
                entries.BeginUpdate(); entries.Items.Clear(); entries.Items.AddRange(lines.Cast<object>().ToArray()); entries.EndUpdate();
                if (sel < entries.Items.Count) entries.SelectedIndex = sel;
                swatches.Invalidate();
            }
            string st = $"{m.Console}: {what}; frame {m.Frame}";
            if (status.Text != st) status.Text = st;
        }

        /// <summary>The console's palettes: one line of values and one row of colours each (read by the engine's
        /// <see cref="DebugPictures.Palettes(DebugMachine, out string)"/>, which the HTTP route and Lite's bridge share).</summary>
        internal static void Read(DebugMachine m, List<string> lines, List<(string, Color[])> rows, out string what)
        {
            foreach (var (label, line, colours) in DebugPictures.Palettes(m, out what))
            {
                lines.Add(line);
                rows.Add((label, colours.Select(c => Color.FromArgb(255, (int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF))).ToArray()));
            }
        }

        private void PaintSwatches(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SystemColors.Control);
            if (rows.Count == 0) return;
            int labelW = 56;
            int maxCols = rows.Max(r => r.colours.Length);
            int cell = Math.Max(8, Math.Min(40, Math.Min((swatches.ClientSize.Width - labelW - 8) / Math.Max(1, maxCols), (swatches.ClientSize.Height - 8) / rows.Count)));
            using var font = new Font("Consolas", 8.5f);
            for (int r = 0; r < rows.Count; r++)
            {
                int y = 4 + r * cell;
                TextRenderer.DrawText(g, rows[r].label, font, new Point(4, y + Math.Max(0, cell / 2 - 7)), SystemColors.ControlText);
                for (int c = 0; c < rows[r].colours.Length; c++)
                {
                    using var b = new SolidBrush(rows[r].colours[c]);
                    var rect = new Rectangle(labelW + c * cell, y, cell - 2, cell - 2);
                    g.FillRectangle(b, rect);
                    g.DrawRectangle(SystemPens.ControlDark, rect);
                }
            }
        }
    }
}
