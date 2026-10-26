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

        /// <summary>The console's palettes: one line of values and one row of colours each.</summary>
        internal static void Read(DebugMachine m, List<string> lines, List<(string, Color[])> rows, out string what)
        {
            var doms = m.Domains();
            IMemoryDomain? D(string id) => doms.FirstOrDefault(x => x.Id == id && x.Unavailable == null && x.Size > 0);
            switch (m.Console)
            {
                case "nes":
                {
                    var ppu = D("PPU");
                    if (ppu == null) { what = "no PPU space (this PPU core has no probe)"; return; }
                    for (int p = 0; p < 8; p++)
                    {
                        var idx = Enumerable.Range(0, 4).Select(i => ppu.Peek(0x3F00 + p * 4 + i) & 0x3F).ToArray();
                        string label = (p < 4 ? "BG" : "SP") + (p & 3);
                        lines.Add($"{label}: {string.Join(" ", idx.Select(x => "$" + x.ToString("X2")))} | {string.Join(" ", idx.Select(x => "#" + NesEmulator.Mix.NesPalette.Rgb[x].ToString("X6")))}");
                        rows.Add((label, idx.Select(x => Rgb(NesEmulator.Mix.NesPalette.Rgb[x])).ToArray()));
                    }
                    what = "palette RAM (PPU $3F00-$3F1F) through the NES colours";
                    return;
                }
                case "snes":
                case "widemess":
                {
                    var cg = D("CGRAM");
                    if (cg == null) { what = "no CGRAM"; return; }
                    int n = Math.Min(256, cg.Size / 2);
                    for (int r = 0; r * 16 < n; r++)
                    {
                        var w = Enumerable.Range(0, 16).Select(i => cg.Peek((r * 16 + i) * 2) | cg.Peek((r * 16 + i) * 2 + 1) << 8).ToArray();
                        string label = "row " + r.ToString("X");
                        lines.Add($"{label}: {string.Join(" ", w.Select(x => x.ToString("X4")))}");
                        rows.Add((label, w.Select(Bgr555).ToArray()));
                    }
                    what = $"CGRAM, {n} colours (BGR555)";
                    return;
                }
                case "gb":
                case "gbc":
                {
                    var io = D("IO");
                    if (io == null) { what = "no I/O registers"; return; }
                    foreach (var (name, reg) in new[] { ("BGP", 0x47), ("OBP0", 0x48), ("OBP1", 0x49) })
                    {
                        int v = io.Peek(reg);
                        var shades = Enumerable.Range(0, 4).Select(i => v >> (2 * i) & 3).ToArray();
                        lines.Add($"{name} ${v:X2}: {string.Join(" ", shades)}");
                        rows.Add((name, shades.Select(s => DmgGreys[s]).ToArray()));
                    }
                    var cram = D("CRAM");
                    if (cram != null)
                    {
                        for (int p = 0; p < 16; p++)
                        {
                            var w = Enumerable.Range(0, 4).Select(i => cram.Peek(p * 8 + i * 2) | cram.Peek(p * 8 + i * 2 + 1) << 8).ToArray();
                            string label = (p < 8 ? "BG" : "OBJ") + (p & 7);
                            lines.Add($"{label}: {string.Join(" ", w.Select(x => x.ToString("X4")))}");
                            rows.Add((label, w.Select(Bgr555).ToArray()));
                        }
                    }
                    what = cram != null ? "BGP / OBP0 / OBP1 and the colour RAM" : "BGP / OBP0 / OBP1 (greys; a monochrome game has no colour RAM)";
                    return;
                }
                default:
                    what = $"no palette view for {m.Console}";
                    return;
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
