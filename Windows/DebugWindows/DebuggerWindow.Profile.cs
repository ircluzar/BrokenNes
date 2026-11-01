using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using NesEmulator.Debugging.Timeflow;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// The debugger window on a kept profile (BrokenDev T3, the Timeflow window's "Profile selection"): no live game, nothing attached or armed.
    /// The trace of a profiled frame (every instruction of the chosen CPU, Mesen's columns), a frame scrubber, the registers of the chosen
    /// instruction and its call stack (from the profile's records), and "go to #N" (a position another answer named).
    /// </summary>
    public sealed partial class DebuggerWindow
    {
        private readonly ProfileView? profile;
        private readonly NumericUpDown profFrame = new() { Name = "profileFrame", AccessibleName = "profileFrame", Width = 100 };
        private readonly TextBox profGoto = new() { Name = "profileGoto", AccessibleName = "profileGoto", Width = 110 };
        private List<ProfInstr> profShown = new();

        /// <summary>Opens on a kept profile (the profile view belongs to the caller).</summary>
        internal DebuggerWindow(ProfileView view)
        {
            profile = view;
            hub = () => null; game = () => null; romPath = () => null;
            var m = view.Meta;
            Text = $"BrokenNes Debugger: profile {m.Id} (timeline {m.Timeline}, frames {m.From}-{m.LastFrameStored})";
            Name = "DebuggerWindowProfile";
            ClientSize = new Size(1100, 620);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            var mono = new Font("Consolas", 9.5f);
            disasm.Font = mono; regs.Font = mono; stack.Font = mono;
            disasm.AccessibleName = "profileTrace"; disasm.Name = "profileTrace";
            regs.AccessibleName = "profileRegisters"; regs.Name = "profileRegisters";
            stack.AccessibleName = "profileStack"; stack.Name = "profileStack";

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4), WrapContents = false };
            bar.Controls.Add(new Label { Text = "Frame", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            bar.Controls.Add(profFrame);
            bar.Controls.Add(new Label { Text = "CPU", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            bar.Controls.Add(cpuBox);
            bar.Controls.Add(new Label { Text = "Go to #", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            bar.Controls.Add(profGoto);
            bar.Controls.Add(Btn("Go", "profileGo", (_, _) => GoTo()));
            profGoto.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; GoTo(); } };

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            right.Controls.Add(Group("The chosen instruction (registers before it ran)", regs), 0, 0);
            right.Controls.Add(Group("Call stack at it (innermost first)", stack), 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 720 };
            split.Panel1.Controls.Add(Group("Trace of the frame (PC, instruction, [address] = value, registers, V, H, Fr, Cyc, #position)", disasm));
            split.Panel2.Controls.Add(right);
            Controls.Add(split);
            Controls.Add(lastBreak);
            Controls.Add(bar);
            Controls.Add(status);

            foreach (var id in view.CpuIds) cpuBox.Items.Add(id);
            cpuBox.SelectedIndex = 0;
            long first = view.Reader.FirstFrame, last = view.Reader.LastFrame;
            profFrame.Minimum = Math.Max(0, first); profFrame.Maximum = Math.Max(profFrame.Minimum, last); profFrame.Value = profFrame.Minimum;
            lastBreak.Text = $"profile {m.Id}: {m.Console}, frames {m.From}-{m.LastFrameStored} stored ({m.Status}{(m.Reason.Length > 0 ? ": " + m.Reason : "")}), " +
                             $"{m.Instructions:N0} instructions, {m.Accesses:N0} accesses{(m.OffFrame != null ? $"; OUT OF SYNC from frame {m.OffFrame} ({m.OffWhat})" : "")}";
            profFrame.ValueChanged += (_, _) => ShowProfileFrame();
            // a frame typed goes there at once (scrubbing; the box's own commit waits for Enter or leaving it)
            profFrame.TextChanged += (_, _) => { if (decimal.TryParse(profFrame.Text, out var v) && v != profFrame.Value && v >= profFrame.Minimum && v <= profFrame.Maximum) profFrame.Value = v; };
            cpuBox.SelectedIndexChanged += (_, _) => ShowProfileFrame();
            disasm.SelectedIndexChanged += (_, _) => ShowProfileInstruction();
            Shown += (_, _) => ShowProfileFrame();
        }

        private void ShowProfileFrame(long selectSeq = -1)
        {
            if (profile == null || IsDisposed) return;
            try
            {
                var fv = profile.Frame((long)profFrame.Value);
                int cpu = Math.Max(0, cpuBox.SelectedIndex);
                profShown = fv.Instructions.Where(i => i.Cpu == cpu).ToList();
                disasm.BeginUpdate();
                disasm.Items.Clear();
                foreach (var i in profShown) disasm.Items.Add(profile.TraceLine(in i));
                disasm.EndUpdate();
                status.Text = $"frame {fv.Frame}: {profShown.Count:N0} instructions of {cpuBox.SelectedItem} ({fv.Instructions.Count:N0} in all), {fv.Accesses:N0} accesses ({fv.Writes:N0} writes)";
                int at = selectSeq >= 0 ? profShown.FindIndex(i => i.Seq == selectSeq) : 0;
                if (at >= 0 && at < disasm.Items.Count) disasm.SelectedIndex = at;
            }
            catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or ArgumentException) { status.Text = "profile: " + e.Message; }
        }

        private void ShowProfileInstruction()
        {
            if (profile == null) return;
            int k = disasm.SelectedIndex;
            if (k < 0 || k >= profShown.Count) return;
            var i = profShown[k];
            regs.Text = $"#{i.Seq}  frame {i.Frame}  line {i.R.Scanline} dot {i.R.Dot}  cycle {i.R.Cycle}\r\n{profile.Ctx.Address(i.Cpu, i.R.Pc)}  {profile.Ctx.Disasm(in i)}\r\n{profile.Registers(in i)}" +
                        (profile.Ctx.SymbolOf(in i) is { } s ? $"\r\nin {s}" : "");
            var st = profile.StackAt(i.Seq);
            stack.BeginUpdate();
            stack.Items.Clear();
            if (st["instruction"]?["stack"] is JsonArray a) foreach (var f in a) stack.Items.Add(f?.ToString() ?? "");
            if (stack.Items.Count == 0) stack.Items.Add("(no call frame known: top level, or entered before the re-run began)");
            stack.EndUpdate();
        }

        private void GoTo()
        {
            if (profile == null) return;
            if (!long.TryParse(profGoto.Text.Trim().TrimStart('#'), out long seq)) { status.Text = "go to: a position (#N from an answer)"; return; }
            long f = profile.Reader.FrameOfSeq(seq);
            if (f < 0) { status.Text = $"#{seq} is not in this profile"; return; }
            var fv = profile.Frame(f);
            var hit = fv.Instructions.FindIndex(i => i.Seq == seq);
            if (hit < 0) { status.Text = $"#{seq} is not in this profile"; return; }
            int cpu = fv.Instructions[hit].Cpu;
            if (cpuBox.SelectedIndex != cpu) cpuBox.SelectedIndex = cpu;
            if (profFrame.Value != f) profFrame.Value = f;
            ShowProfileFrame(seq);
        }
    }
}
