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
    /// The event view (BrokenDev D8, "D8 settled"): one frame's register writes with their scanline and dot, DMA / HDMA transfers (a run of
    /// bytes to one register is one line with its count) and interrupt entries. It is a <see cref="DebugObserver"/> on the app's debugger,
    /// added when the window opens (attaching the debugger if nobody had) and removed when it closes (detaching the debugger it attached):
    /// closed, nothing is armed.
    /// </summary>
    public sealed class EventsWindow : Form
    {
        private readonly Func<DebuggerHub?> hub;
        private readonly Func<object?> game;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
        private readonly ListBox list = new() { Name = "events", AccessibleName = "events", Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Consolas", 9f) };
        private readonly TextBox info = new() { Name = "info", AccessibleName = "info", Dock = DockStyle.Bottom, ReadOnly = true };
        private readonly CheckBox freeze = new() { Name = "freeze", AccessibleName = "freeze", Text = "Hold this frame", AutoSize = true, Dock = DockStyle.Top };
        private Recorder? rec;
        private Debugger? attachedTo, attachedHere;
        private long shownFrame = -1;

        public EventsWindow(Func<DebuggerHub?> hub, Func<object?> game)
        {
            this.hub = hub; this.game = game;
            Text = "BrokenNes Events";
            Name = "EventsWindow";
            ClientSize = new Size(620, 600);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            Controls.Add(list);
            Controls.Add(freeze);
            Controls.Add(info);
            timer.Tick += (_, _) => RefreshView();
            Shown += (_, _) => { RefreshView(); timer.Start(); };
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); Release(); };
        }

        /// <summary>The observer: runs on the emulation thread.</summary>
        internal sealed class Recorder : DebugObserver
        {
            private readonly DebugMachine m;
            private readonly string family;
            private readonly object sync = new();
            private List<string> current = new(), last = new();
            private long frame = -1, lastFrame = -1;
            private string? runKey; private int runCount; private string runHead = "";
            public int Dropped;
            public Recorder(DebugMachine m) { this.m = m; family = DebugPictures.Family(m.Console); }
            public override bool WantsSteps(int cpuIndex) => cpuIndex == 0;
            public override bool WantsAccesses(int cpuIndex) => cpuIndex == 0;

            private bool IsRegister(uint a) => family switch
            {
                "nes" => a >= 0x2000 && a < 0x4020,
                "snes" => ((a >> 16) & 0x40) == 0 && ((a & 0xFFFF) is >= 0x2100 and < 0x2200 or >= 0x4200 and < 0x4380),
                "gb" => a >= 0xFF00 && a < 0xFF80 || a == 0xFFFF,
                _ => false,
            };

            private void Roll()
            {
                long f = m.Frame;
                if (f == frame) return;
                Flush();
                if (frame >= 0) { lock (sync) { last = current; lastFrame = frame; } current = new List<string>(); }
                frame = f;
            }

            private void Flush()
            {
                if (runKey == null) return;
                Add(runCount > 1 ? $"{runHead} x{runCount}" : runHead);
                runKey = null; runCount = 0;
            }

            private void Add(string s) { if (current.Count < 5000) current.Add(s); else Dropped++; }

            private string Where() => $"line {m.Scanline,3} dot {m.Dot,3}";

            public override void OnStep(DebugCpu cpu, long cycle, bool executes, string? interrupt)
            {
                Roll();
                if (interrupt == null) return;
                Flush();
                Add($"{Where()}  interrupt {interrupt}");
            }

            public override void OnAccess(DebugCpu cpu, uint address, byte value, bool write, AccessSource source, int channel)
            {
                if (!write) return;
                bool dma = source != AccessSource.Cpu;
                if (!dma && !IsRegister(address)) return;
                Roll();
                string reg = "$" + address.ToString(family == "snes" ? "X6" : "X4");
                if (dma)
                {
                    string key = $"{source}:{channel}:{(IsRegister(address) ? reg : "mem")}";
                    if (key == runKey) { runCount++; return; }
                    Flush();
                    runKey = key; runCount = 1;
                    runHead = $"{Where()}  {source}{(channel >= 0 ? " ch" + channel : "")} -> {(IsRegister(address) ? reg : reg + " (memory)")} first ${value:X2}";
                    return;
                }
                Flush();
                Add($"{Where()}  write {reg} = ${value:X2}  (pc {cpu.FormatAddress(cpu.PC)})");
            }

            public (long frame, string[] lines) Last() { lock (sync) return (lastFrame, last.ToArray()); }
        }

        private void Release()
        {
            var d = attachedTo;
            if (d != null && rec != null) d.RemoveObserver(rec);
            var h = hub();
            if (h != null && attachedHere != null && ReferenceEquals(h.Current(game()), attachedHere)) h.Detach();
            rec = null; attachedTo = null; attachedHere = null;
        }

        private void RefreshView()
        {
            if (IsDisposed) return;
            var h = hub(); string? why = null;
            var g = game();
            var m = h?.Machine(g, out why);
            if (h == null || m == null) { info.Text = h == null ? "the app's debugger hub is not up yet" : "no game: " + why; return; }
            var cur = h.Current(g);
            if (rec == null || attachedTo == null || !ReferenceEquals(cur, attachedTo) || !ReferenceEquals(attachedTo.Machine, m))
            {
                // a new game (or the debugger detached by someone else): observe again
                if (attachedTo != null && rec != null && ReferenceEquals(cur, attachedTo)) attachedTo.RemoveObserver(rec);
                try
                {
                    var before = h.Current(g);
                    var d = h.Attach(g);
                    attachedHere = before == null ? d : (ReferenceEquals(attachedHere, d) ? d : null);
                    rec = new Recorder(d.Machine);
                    d.AddObserver(rec);
                    attachedTo = d;
                }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { info.Text = e.Message; return; }
            }
            if (freeze.Checked) return;
            var (f, lines) = rec.Last();
            if (f == shownFrame) return;
            shownFrame = f;
            list.BeginUpdate(); list.Items.Clear(); list.Items.AddRange(lines.Cast<object>().ToArray()); list.EndUpdate();
            int writes = lines.Count(x => x.Contains(" write ")), irqs = lines.Count(x => x.Contains(" interrupt ")), dmas = lines.Length - writes - irqs;
            info.Text = $"{m.Console}: frame {f}: {writes} register writes, {dmas} DMA runs, {irqs} interrupts{(rec.Dropped > 0 ? $"; {rec.Dropped} events past 5000 a frame dropped" : "")}";
        }
    }
}
