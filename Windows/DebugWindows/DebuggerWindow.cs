using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BrokenNes.Windows.WebApi;
using NesEmulator.Debugging;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// The debugger window (BrokenDev D8, docs/projects/brokendev-debugging-plan.md "D8 settled"): registers, the disassembly around the PC,
    /// breakpoints, step / continue and the call stack, over the D1 engine through the app's one <see cref="DebuggerHub"/> (the same debugger the
    /// HTTP API and an agent's live session see). Reading arms nothing; the first breakpoint, pause or step attaches the debugger, and closing the
    /// window detaches the one it attached, so the game runs on exactly as without it. The refresh timer lives only while the window is open.
    /// </summary>
    public sealed class DebuggerWindow : Form
    {
        private readonly Func<DebuggerHub?> hub;
        private readonly Func<object?> game;
        private readonly Func<string?> romPath;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
        private Debugger? attachedHere;      // the debugger this window attached (closing detaches it)
        private object? symbolsLoadedFor;    // the debugger whose symbols this window loaded from beside the ROM

        private readonly ComboBox cpuBox = new() { Name = "cpu", AccessibleName = "cpu", DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly ListBox disasm = new() { Name = "disassembly", AccessibleName = "disassembly", Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox regs = new() { Name = "registers", AccessibleName = "registers", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false };
        private readonly ListBox stack = new() { Name = "callstack", AccessibleName = "callstack", Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly ListBox bps = new() { Name = "breakpoints", AccessibleName = "breakpoints", Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox bpSpec = new() { Name = "bpspec", AccessibleName = "bpspec", Width = 150 };
        private readonly ComboBox bpKind = new() { Name = "bpkind", AccessibleName = "bpkind", DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
        private readonly TextBox symPath = new() { Name = "sympath", AccessibleName = "sympath", Width = 260 };
        private readonly Label status = new() { Name = "status", AccessibleName = "status", Dock = DockStyle.Bottom, Height = 36, AutoEllipsis = true };
        private readonly Label lastBreak = new() { Name = "lastbreak", AccessibleName = "lastbreak", Dock = DockStyle.Top, Height = 34, AutoEllipsis = true };

        private string lastDisasm = "", lastStack = "", lastBps = "";

        public DebuggerWindow(Func<DebuggerHub?> hub, Func<object?> game, Func<string?> romPath)
        {
            this.hub = hub; this.game = game; this.romPath = romPath;
            Text = "BrokenNes Debugger";
            Name = "DebuggerWindow";
            ClientSize = new Size(980, 620);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            var mono = new Font("Consolas", 9.5f);
            disasm.Font = mono; regs.Font = mono; stack.Font = mono; bps.Font = mono;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4), WrapContents = false };
            bar.Controls.Add(Btn("Pause", "pause", (_, _) => Act(d => d.Pause())));
            bar.Controls.Add(Btn("Continue", "continue", (_, _) => { var d = Current(); d?.Continue(); }));
            bar.Controls.Add(Btn("Step Into", "stepinto", (_, _) => Act(d => d.StepInto(Cpu))));
            bar.Controls.Add(Btn("Step Over", "stepover", (_, _) => Act(d => d.StepOver(Cpu))));
            bar.Controls.Add(Btn("Step Out", "stepout", (_, _) => Act(d => d.StepOut(Cpu))));
            bar.Controls.Add(Btn("Step Frame", "stepframe", (_, _) => Act(d => d.StepFrame())));
            bar.Controls.Add(new Label { Text = "CPU", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            bar.Controls.Add(cpuBox);
            bar.Controls.Add(Btn("Detach", "detach", (_, _) => DetachOwn()));

            var symBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4), WrapContents = false };
            symBar.Controls.Add(new Label { Text = "Symbols", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            symBar.Controls.Add(symPath);
            symBar.Controls.Add(Btn("Load", "loadsymbols", (_, _) => LoadSymbols(symPath.Text)));
            symBar.Controls.Add(Btn("Browse...", "browsesymbols", (_, _) =>
            {
                using var dlg = new OpenFileDialog { Filter = "Symbols (*.mlb;*.sym;*.noi)|*.mlb;*.sym;*.noi|All files|*.*" };
                if (dlg.ShowDialog(this) == DialogResult.OK) { symPath.Text = dlg.FileName; LoadSymbols(dlg.FileName); }
            }));

            bpKind.Items.AddRange(new object[] { "exec", "read", "write", "rw" });
            bpKind.SelectedIndex = 0;
            var bpBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, WrapContents = false };
            bpBar.Controls.Add(bpSpec);
            bpBar.Controls.Add(bpKind);
            bpBar.Controls.Add(Btn("Add", "addbreakpoint", (_, _) => AddBreakpoint()));
            bpBar.Controls.Add(Btn("Remove", "removebreakpoint", (_, _) => RemoveBreakpoint()));
            bpSpec.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddBreakpoint(); } };

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            right.Controls.Add(Group("Registers", regs), 0, 0);
            right.Controls.Add(Group("Call stack (innermost first)", stack), 0, 1);
            var bpGroup = Group("Breakpoints (address, range, bank:address, space:address or symbol)", bps);
            bpGroup.Controls.Add(bpBar);
            right.Controls.Add(bpGroup, 0, 2);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 560 };
            split.Panel1.Controls.Add(Group("Disassembly (> the PC)", disasm));
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            Controls.Add(lastBreak);
            Controls.Add(symBar);
            Controls.Add(bar);
            Controls.Add(status);

            cpuBox.SelectedIndexChanged += (_, _) => { lastDisasm = ""; RefreshView(); };
            timer.Tick += (_, _) => RefreshView();
            Shown += (_, _) => { RefreshView(); timer.Start(); };
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); DetachOwn(); };
        }

        private static Button Btn(string text, string name, EventHandler click)
        {
            var b = new Button { Text = text, Name = name, AccessibleName = name, AutoSize = true, Height = 24 };
            b.Click += click;
            return b;
        }

        private static GroupBox Group(string text, Control c) { var g = new GroupBox { Text = text, Dock = DockStyle.Fill }; g.Controls.Add(c); return g; }

        private string Cpu => cpuBox.SelectedItem as string ?? "main";

        private Debugger? Current() => hub()?.Current(game());

        /// <summary>Attaches (on the first use) and runs the action; the window remembers the debugger it attached.</summary>
        private void Act(Action<Debugger> a)
        {
            var h = hub();
            if (h == null) { Note("the app's debugger hub is not up yet (the HTTP server starts it)"); return; }
            try
            {
                var before = h.Current(game());
                var d = h.Attach(game());
                if (before == null) attachedHere = d;
                AutoSymbols(d);
                a(d);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException) { Note(e.Message); }
            RefreshView();
        }

        /// <summary>Symbols beside the ROM (game.mlb / game.sym / game.noi), loaded once per debugger.</summary>
        private void AutoSymbols(Debugger d)
        {
            if (ReferenceEquals(symbolsLoadedFor, d)) return;
            symbolsLoadedFor = d;
            if (d.Symbols.All.Count > 0) return;
            string? rom = romPath();
            if (string.IsNullOrEmpty(rom)) return;
            string? p = SymbolFileBeside(rom, d.Machine.Console);
            if (p == null) return;
            try { int n = d.Symbols.Load(p, d.Machine.Console); if (symPath.Text.Length == 0) symPath.Text = p; Console.WriteLine($"[debugger] {n} symbols from {p}"); }
            catch (Exception e) { Console.WriteLine($"[debugger] symbols {p}: {e.Message}"); }
        }

        /// <summary>The symbol file beside a ROM for its console, or null: the NES's NESFab / Mesen .mlb; the SNES's and WideMess's WLA-DX .sym;
        /// the Game Boy's .sym or GBDK .noi. Only that console's kind: a SNES game.sym beside a NES game.nes is another game's.</summary>
        internal static string? SymbolFileBeside(string rom, string console)
        {
            string[] exts = console.ToLowerInvariant() switch
            {
                "nes" => new[] { ".mlb" },
                "snes" or "widemess" or "wms" => new[] { ".sym" },
                "gb" or "gbc" => new[] { ".sym", ".noi" },
                _ => Array.Empty<string>(),
            };
            foreach (var ext in exts) { string p = Path.ChangeExtension(rom, ext); if (File.Exists(p)) return p; }
            return null;
        }

        private void LoadSymbols(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Act(d =>
            {
                try { int n = d.Symbols.Load(path.Trim().Trim('"'), d.Machine.Console); Note($"{n} symbols loaded ({d.Symbols.All.Count} in all)"); }
                catch (Exception e) { Note("symbols: " + e.Message); }
            });
            lastDisasm = "";
        }

        private void AddBreakpoint()
        {
            string spec = bpSpec.Text.Trim();
            if (spec.Length == 0) return;
            var kind = (bpKind.SelectedItem as string) switch { "read" => AccessKind.Read, "write" => AccessKind.Write, "rw" => AccessKind.ReadWrite, _ => AccessKind.Exec };
            Act(d => { var b = d.Add(spec, kind, cpu: Cpu); Note("breakpoint " + b); bpSpec.Text = ""; });
        }

        private void RemoveBreakpoint()
        {
            var d = Current();
            if (d == null || bps.SelectedIndex < 0) return;
            var list = d.Breakpoints;
            if (bps.SelectedIndex < list.Count) d.Remove(list[bps.SelectedIndex].Id);
            RefreshView();
        }

        /// <summary>Detaches the debugger this window attached (nothing else's: an agent's live session keeps its own).</summary>
        private void DetachOwn()
        {
            var h = hub();
            if (h == null || attachedHere == null) return;
            if (ReferenceEquals(h.Current(game()), attachedHere)) h.Detach();
            attachedHere = null; symbolsLoadedFor = null;
        }

        // ---------------------------------------------------------------- the view

        private void RefreshView()
        {
            if (IsDisposed) return;
            var h = hub();
            var g = game();
            string? why = null;
            var m = h?.Machine(g, out why);
            if (h == null) { SetStatus("the app's debugger hub is not up yet"); return; }
            if (m == null) { SetStatus("no game: " + why); return; }

            var ids = m.Cpus.Select(c => c.Id).ToArray();
            if (!cpuBox.Items.Cast<string>().SequenceEqual(ids))
            {
                cpuBox.Items.Clear(); cpuBox.Items.AddRange(ids); cpuBox.SelectedIndex = 0;
            }
            var c = m.Cpu(Cpu) ?? m.Main;
            var d = h.Current(g);
            if (d != null && attachedHere != null && !ReferenceEquals(d, attachedHere)) attachedHere = null;   // a new game: someone else's debugger now

            // registers
            var sb = new System.Text.StringBuilder();
            sb.Append(c.Name).Append("  PC ").Append(c.FormatAddress(c.PC));
            string? at = d?.SymbolAt(c, c.PC);
            if (at != null) sb.Append("  ").Append(at);
            sb.AppendLine();
            foreach (var r in c.RegisterNames.Distinct())
                if (c.TryGetRegister(r, out long v)) sb.Append(r).Append(" = $").Append(v.ToString("X")).AppendLine();
            string rt = sb.ToString();
            if (regs.Text != rt) regs.Text = rt;

            // disassembly
            var lines = Disassemble(c, d, 8, 24, out int pcLine);
            string dk = string.Join("\n", lines);
            if (dk != lastDisasm)
            {
                lastDisasm = dk;
                disasm.BeginUpdate(); disasm.Items.Clear(); disasm.Items.AddRange(lines.Cast<object>().ToArray()); disasm.EndUpdate();
                if (pcLine >= 0) { disasm.SelectedIndex = pcLine; disasm.TopIndex = Math.Max(0, pcLine - 6); }
            }

            // call stack (innermost first, as the MCP tools show it)
            string[] st = d == null ? new[] { "(not tracked: no debugger attached; Pause, a step or a breakpoint attaches one)" }
                : SafeStack(d, c.Id);
            string sk = string.Join("\n", st);
            if (sk != lastStack) { lastStack = sk; stack.BeginUpdate(); stack.Items.Clear(); stack.Items.AddRange(st.Cast<object>().ToArray()); stack.EndUpdate(); }

            // breakpoints
            var bl = d?.Breakpoints.Select(b => $"#{b.Id} {b} hits {b.Hits}").ToArray() ?? Array.Empty<string>();
            string bk = string.Join("\n", bl);
            if (bk != lastBps) { int sel = bps.SelectedIndex; lastBps = bk; bps.BeginUpdate(); bps.Items.Clear(); bps.Items.AddRange(bl.Cast<object>().ToArray()); bps.EndUpdate(); if (sel < bps.Items.Count) bps.SelectedIndex = sel; }

            var lb = d?.LastBreak;
            string lbt = lb == null ? "last break: none" : $"last break: {lb.Reason} at {c.FormatAddress(lb.Pc)} {lb.PcSymbol} (frame {lb.Frame}, line {lb.Scanline}) {lb.Text}";
            if (lastBreak.Text != lbt) lastBreak.Text = lbt;

            string state = d == null ? "detached (the game runs with no debugger)" : d.IsParked ? "PARKED" : d.IsArmed ? "running, armed" : "running";
            string syms = d == null ? "" : $", {d.Symbols.All.Count} symbols";
            string stt = $"{m.Console}: {state}{syms}; frame {m.Frame}, scanline {m.Scanline}";
            SetStatus(stt);
        }

        private string? note; private long noteUntil;
        /// <summary>A message the status line shows for three seconds before the machine's state comes back.</summary>
        private void Note(string text) { note = text; noteUntil = Environment.TickCount64 + 3000; status.Text = text; }
        private void SetStatus(string text)
        {
            string t = note != null && Environment.TickCount64 < noteUntil ? note + "  |  " + text : text;
            if (status.Text != t) status.Text = t;
        }

        private static string[] SafeStack(Debugger d, string cpu)
        {
            try
            {
                var s = d.CallStack(cpu);
                if (s.Count == 0) return new[] { "(empty)" };
                return s.Reverse().Select(f => f.ToString()).ToArray();
            }
            catch (ArgumentException e) { return new[] { e.Message }; }
        }

        /// <summary>Lines around the PC: <paramref name="before"/> lines found by scanning back for a start that decodes onto the PC (the usual
        /// guess for variable-length code), then <paramref name="after"/> from the PC. The PC's line starts with "&gt;".</summary>
        internal static List<string> Disassemble(DebugCpu c, Debugger? d, int before, int after, out int pcLine)
        {
            uint pc = c.PC;
            uint start = pc;
            int bestCount = 0;
            for (int back = 1; back <= before * 3; back++)
            {
                uint s = (uint)(pc - back) & (c.AddressDigits == 6 ? 0xFFFFFFu : 0xFFFFu);
                if (c.AddressDigits == 6 && (s >> 16) != (pc >> 16)) break;   // the 65816 stays in its bank
                uint a = s; int n = 0; bool hit = false;
                for (int k = 0; k < before * 3 && n <= before; k++)
                {
                    if (a == pc) { hit = true; break; }
                    var ic = c.Classify(c.FetchBytes(a));
                    a = c.NextAddress(a, Math.Max(1, ic.Length)); n++;
                    if (((a - s) & 0xFFFF) > (uint)back) break;
                }
                if (hit && n <= before && n > bestCount) { bestCount = n; start = s; }
                if (bestCount == before) break;
            }
            var lines = new List<string>();
            pcLine = -1;
            uint p = start;
            for (int i = 0; i < bestCount + after; i++)
            {
                uint bytes = c.FetchBytes(p);
                var ic = c.Classify(bytes);
                int len = Math.Max(1, ic.Length);
                var b = c.InstructionBytes(p, len);
                string text = c.Disassembler.Format(p, b, c.Context, d == null ? null : x => { var s = d.SymbolAt(c, x); return s != null && !s.Contains('+') ? s : null; });
                string? label = d?.SymbolAt(c, p);
                if (label != null && !label.Contains('+')) lines.Add($"           {label}:");
                if (p == pc) pcLine = lines.Count;
                lines.Add($"{(p == pc ? ">" : " ")} {c.FormatAddress(p),-8} {string.Join(" ", b.Select(x => x.ToString("X2"))),-12} {text}");
                p = c.NextAddress(p, len);
            }
            return lines;
        }
    }
}
