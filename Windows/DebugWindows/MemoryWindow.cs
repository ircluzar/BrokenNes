using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using BrokenNes.Windows.WebApi;
using NesEmulator;
using NesEmulator.Corruption;
using NesEmulator.Debugging;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// The memory viewer (BrokenDev D8, "D8 settled"): every memory domain of the running console (the same list as GET /api/memory/domains:
    /// the NES's RAM, PRG, CHR, nametables, PPU space; the SNES's WRAM, VRAM, CGRAM, OAM, ARAM, registers; the Game Boy's WRAM, VRAM, OAM,
    /// HRAM, I/O ...), live hex and ASCII with the bytes that changed since the last refresh in red, a go-to box (an address in the domain or
    /// a symbol), and watches (an address, a range or a symbol, with its value). It reads through <see cref="DebuggerHub.Machine"/> and keeps
    /// its own symbol table, so it never attaches a debugger and arms nothing; its timer lives only while it is open.
    /// </summary>
    public sealed class MemoryWindow : Form
    {
        private const int Rows = 16;
        private readonly Func<DebuggerHub?> hub;
        private readonly Func<object?> game;
        private readonly Func<string?> romPath;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };

        private readonly ComboBox domainBox = new() { Name = "domain", AccessibleName = "domain", DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly TextBox gotoBox = new() { Name = "goto", AccessibleName = "goto", Width = 140 };
        private readonly ListBox hex = new() { Name = "hex", AccessibleName = "hex", Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed };
        private readonly VScrollBar scroll = new() { Name = "scroll", Dock = DockStyle.Right };
        private readonly ListBox watches = new() { Name = "watches", AccessibleName = "watches", Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox watchBox = new() { Name = "watchspec", AccessibleName = "watchspec", Width = 160 };
        private readonly Label status = new() { Name = "status", AccessibleName = "status", Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true };

        private readonly List<string> watchSpecs = new();
        private SymbolTable symbols = new();
        private object? symbolsFor;          // the machine view whose symbols are loaded
        private string? symbolsRom;          // and its ROM file
        private object? domainsFor;          // the machine the domain list was made for
        private IReadOnlyList<IMemoryDomain> domains = Array.Empty<IMemoryDomain>();
        private int offset;                  // the first row's offset in the domain
        private byte[] shown = Array.Empty<byte>(), previous = Array.Empty<byte>();
        private bool[] changed = Array.Empty<bool>();
        private string lastWatches = "";

        public MemoryWindow(Func<DebuggerHub?> hub, Func<object?> game, Func<string?> romPath)
        {
            this.hub = hub; this.game = game; this.romPath = romPath;
            Text = "BrokenNes Memory";
            Name = "MemoryWindow";
            ClientSize = new Size(760, 520);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            hex.Font = new Font("Consolas", 9.5f);
            hex.ItemHeight = hex.Font.Height + 2;
            watches.Font = hex.Font;
            hex.DrawItem += DrawRow;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4), WrapContents = false };
            bar.Controls.Add(new Label { Text = "Domain", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            bar.Controls.Add(domainBox);
            bar.Controls.Add(new Label { Text = "Go to", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            bar.Controls.Add(gotoBox);
            var go = new Button { Text = "Go", Name = "gobutton", AccessibleName = "gobutton", AutoSize = true, Height = 24 };
            go.Click += (_, _) => GoTo(gotoBox.Text);
            bar.Controls.Add(go);
            gotoBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; GoTo(gotoBox.Text); } };

            var wbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, WrapContents = false };
            wbar.Controls.Add(watchBox);
            var add = new Button { Text = "Watch", Name = "addwatch", AccessibleName = "addwatch", AutoSize = true, Height = 24 };
            add.Click += (_, _) => AddWatch();
            var rem = new Button { Text = "Remove", Name = "removewatch", AccessibleName = "removewatch", AutoSize = true, Height = 24 };
            rem.Click += (_, _) => { int i = watches.SelectedIndex; if (i >= 0 && i < watchSpecs.Count) { watchSpecs.RemoveAt(i); RefreshView(); } };
            wbar.Controls.Add(add); wbar.Controls.Add(rem);
            watchBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddWatch(); } };

            var hexPanel = new Panel { Dock = DockStyle.Fill };
            hexPanel.Controls.Add(hex); hexPanel.Controls.Add(scroll);
            var wGroup = new GroupBox { Text = "Watches (an address, a range, space:address or a symbol)", Dock = DockStyle.Fill };
            wGroup.Controls.Add(watches); wGroup.Controls.Add(wbar);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330 };
            split.Panel1.Controls.Add(hexPanel);
            split.Panel2.Controls.Add(wGroup);

            Controls.Add(split);
            Controls.Add(bar);
            Controls.Add(status);

            domainBox.SelectedIndexChanged += (_, _) => { offset = 0; previous = Array.Empty<byte>(); ConfigureScroll(); RefreshView(); };
            scroll.ValueChanged += (_, _) => { offset = scroll.Value * 16; previous = Array.Empty<byte>(); RefreshView(); };
            hex.MouseWheel += (_, e) => { int v = Math.Clamp(scroll.Value - Math.Sign(e.Delta) * 4, scroll.Minimum, Math.Max(scroll.Minimum, scroll.Maximum - scroll.LargeChange + 1)); scroll.Value = v; };
            timer.Tick += (_, _) => RefreshView();
            Shown += (_, _) => { RefreshView(); timer.Start(); };
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
        }

        private IMemoryDomain? Domain => domainBox.SelectedIndex >= 0 && domainBox.SelectedIndex < domains.Count ? domains[domainBox.SelectedIndex] : null;

        private void ConfigureScroll()
        {
            int size = Domain?.Size ?? 0;
            int rows = Math.Max(1, (size + 15) / 16);
            scroll.Minimum = 0; scroll.LargeChange = Rows; scroll.SmallChange = 1;
            scroll.Maximum = Math.Max(0, rows - 1);
            scroll.Enabled = rows > Rows;
            int v = Math.Clamp(offset / 16, 0, Math.Max(0, rows - Rows));
            if (scroll.Value != v) scroll.Value = v;
        }

        /// <summary>Symbols beside the ROM (game.mlb / .sym / .noi), loaded into the window's own table once per game.</summary>
        private void EnsureSymbols(DebugMachine m, object? g)
        {
            string? rom = romPath();
            if (ReferenceEquals(symbolsFor, m) && symbolsRom == rom) return;   // a machine view is made again for every game (a NES ROM load too)
            symbolsFor = m; symbolsRom = rom; symbols = new SymbolTable();
            if (string.IsNullOrEmpty(rom)) return;
            string? p = DebuggerWindow.SymbolFileBeside(rom, m.Console);
            if (p != null) { try { symbols.Load(p, m.Console); } catch (Exception e) { Console.WriteLine($"[memory] symbols {p}: {e.Message}"); } }
        }

        private SymbolTable Symbols(DebugMachine m)
        {
            // an attached debugger's table when it has symbols (the user may have loaded others), else the window's own
            var d = hub()?.Current(game());
            return d != null && d.Symbols.All.Count > 0 ? d.Symbols : symbols;
        }

        private void GoTo(string text)
        {
            text = text.Trim();
            var h = hub(); var m = h?.Machine(game(), out _);
            if (m == null || text.Length == 0) return;
            if (DebugExpr.TryParseNumber(text.StartsWith("$") || text.StartsWith("0x") ? text : "$" + text, out long a))
            {
                Show(Domain, (int)a);
                return;
            }
            var t = Symbols(m);
            if (!t.TryFind(text, out var sym, out string? err)) { status.Text = err ?? $"no symbol '{text}'"; return; }
            int idx = FindDomainIndex(sym.Domain);
            if (idx < 0) { status.Text = $"{sym}: no domain '{sym.Domain}' here"; return; }
            if (domainBox.SelectedIndex != idx) domainBox.SelectedIndex = idx;
            int at = (int)sym.Address;
            if (sym.Bank >= 0 && domains[idx].Size > 0x10000 && at < 0x10000) at |= sym.Bank << 16;   // the SNES bus: bank:address
            Show(domains[idx], at);
            status.Text = sym.ToString();
        }

        private int FindDomainIndex(string space)
        {
            string s = space.ToLowerInvariant() switch { "cpu" or "bus" or "ram" => "BUS", "prg" or "rom" => "PRG", "sram" or "prgram" => "PRGRAM", var x => x.ToUpperInvariant() };
            for (int i = 0; i < domains.Count; i++) if (domains[i].Id.Equals(s, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private void Show(IMemoryDomain? d, int address)
        {
            if (d == null) return;
            offset = Math.Clamp(address & ~0xF, 0, Math.Max(0, ((d.Size + 15) / 16 - 1) * 16));
            previous = Array.Empty<byte>();
            ConfigureScroll();
            RefreshView();
        }

        private void AddWatch()
        {
            string s = watchBox.Text.Trim();
            if (s.Length == 0) return;
            watchSpecs.Add(s); watchBox.Text = "";
            RefreshView();
        }

        // ---------------------------------------------------------------- the view

        private void RefreshView()
        {
            if (IsDisposed) return;
            var h = hub(); var g = game();
            string? why = null;
            var m = h?.Machine(g, out why);
            if (m == null) { status.Text = h == null ? "the app's debugger hub is not up yet" : "no game: " + why; return; }
            EnsureSymbols(m, g);
            if (!ReferenceEquals(domainsFor, m))
            {
                domainsFor = m;
                domains = m.Domains().Where(x => x.Unavailable == null && x.Size > 0).ToList();
                string? keep = Domain?.Id;
                domainBox.BeginUpdate(); domainBox.Items.Clear();
                foreach (var d in domains) domainBox.Items.Add($"{d.Id} ({d.Size:N0} bytes) {d.Label}");
                domainBox.EndUpdate();
                int k = keep == null ? -1 : domains.ToList().FindIndex(x => x.Id == keep);
                if (k < 0) k = Math.Max(0, domains.ToList().FindIndex(x => x.Id is "RAM" or "WRAM"));
                offset = 0; previous = Array.Empty<byte>();
                if (domains.Count > 0) domainBox.SelectedIndex = k;
            }
            var dom = Domain;
            if (dom == null) return;

            int n = Math.Max(0, Math.Min(Rows * 16, dom.Size - offset));
            var now = new byte[n];
            for (int i = 0; i < n; i++) { try { now[i] = dom.Peek(offset + i); } catch { now[i] = 0; } }
            var ch = new bool[n];
            if (previous.Length == n) for (int i = 0; i < n; i++) ch[i] = now[i] != previous[i];
            bool same = shown.Length == n && now.AsSpan().SequenceEqual(shown) && !ch.Any(x => x) && !changed.Any(x => x);
            previous = now; changed = ch;
            if (!same || hex.Items.Count == 0)
            {
                shown = now;
                var lines = new List<object>();
                int digits = dom.Size > 0x10000 ? 6 : 4;
                for (int r = 0; r * 16 < n; r++)
                {
                    var sb = new StringBuilder();
                    sb.Append((offset + r * 16).ToString(digits == 6 ? "X6" : "X4")).Append(": ");
                    int cnt = Math.Min(16, n - r * 16);
                    for (int i = 0; i < 16; i++) sb.Append(i < cnt ? now[r * 16 + i].ToString("X2") : "  ").Append(i == 7 ? "  " : " ");
                    sb.Append(' ');
                    for (int i = 0; i < cnt; i++) { byte b = now[r * 16 + i]; sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.'); }
                    lines.Add(sb.ToString());
                }
                hex.BeginUpdate(); hex.Items.Clear(); hex.Items.AddRange(lines.ToArray()); hex.EndUpdate();
            }

            // watches: resolved with the CPU's spaces and the symbols, read without side effects (no debugger needed)
            var wl = new List<string>();
            var t = Symbols(m);
            foreach (var spec in watchSpecs) wl.Add(WatchLine(m, t, spec));
            string wk = string.Join("\n", wl);
            if (wk != lastWatches)
            {
                int sel = watches.SelectedIndex;
                lastWatches = wk;
                watches.BeginUpdate(); watches.Items.Clear(); watches.Items.AddRange(wl.Cast<object>().ToArray()); watches.EndUpdate();
                if (sel < watches.Items.Count) watches.SelectedIndex = sel;
            }
            string st = $"{m.Console}: {dom.Id} {offset:X}-{offset + Math.Max(0, n - 1):X} of {dom.Size:N0} bytes; frame {m.Frame}; {t.All.Count} symbols{(t.Files.Count > 0 ? " from " + string.Join(", ", t.Files.Select(Path.GetFileName)) : "")}";
            if (status.Text != st && !status.Text.Contains(" = ")) status.Text = st;
        }

        internal static string WatchLine(DebugMachine m, SymbolTable t, string spec)
        {
            try
            {
                var c = m.Main;
                var ranges = Debugger.ResolveSpec(c, t, spec, AccessKind.Read, out string resolved);
                var r0 = ranges[0];
                int len = (int)Math.Min(16, r0.End - r0.Start + 1);
                var b = new byte[len];
                for (int i = 0; i < len; i++) b[i] = c.PeekSpace(r0.Space, r0.Start + (uint)i);
                string bytes = string.Join(" ", b.Select(x => x.ToString("X2")));
                string value = len <= 4 ? " = " + b.Select((x, i) => (long)x << (8 * i)).Sum().ToString() : "";
                return $"{spec} ({resolved}): {bytes}{value}";
            }
            catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { return $"{spec}: {e.Message}"; }
        }

        private void DrawRow(object? sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= hex.Items.Count) return;
            string line = (string)hex.Items[e.Index];
            var font = e.Font ?? hex.Font;
            var fore = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : hex.ForeColor;
            // fixed-width text: draw char by char groups so the changed bytes can be red
            float cw = TextRenderer.MeasureText(e.Graphics, "0000000000", font, Size.Empty, TextFormatFlags.NoPadding).Width / 10f;
            int prefix = line.IndexOf(": ", StringComparison.Ordinal) + 2;
            TextRenderer.DrawText(e.Graphics, line, font, e.Bounds.Location, fore, TextFormatFlags.NoPadding);
            for (int i = 0; i < 16; i++)
            {
                int idx = e.Index * 16 + i;
                if (idx >= changed.Length || !changed[idx]) continue;
                int col = prefix + i * 3 + (i >= 8 ? 1 : 0);
                if (col + 2 > line.Length) continue;
                var pt = new Point(e.Bounds.X + (int)Math.Round(col * cw), e.Bounds.Y);
                var rect = new Rectangle(pt, new Size((int)Math.Ceiling(2 * cw), e.Bounds.Height));
                using (var bg = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? SystemColors.Highlight : hex.BackColor)) e.Graphics.FillRectangle(bg, rect);
                TextRenderer.DrawText(e.Graphics, line.Substring(col, 2), font, pt, Color.Red, TextFormatFlags.NoPadding);
            }
        }
    }
}
