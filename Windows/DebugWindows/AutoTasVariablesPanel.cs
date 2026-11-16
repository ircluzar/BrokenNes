using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BrokenNes.Windows.AutoTas;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// The AutoTAS variables stripe: the left-hand twin of the chapter panel. It sits against the main window's left edge and shows the game's
    /// most important RAM variables live (the package's variables.json = the game dossier's list, priority 1 first, formatted per the dossier),
    /// refreshed every frame (~60 Hz) while the TAS replays and while the player has the pad. "Collapse" folds it to a thin strip; "All" shows the
    /// priority 3 variables too. The tooltip of a row is the dossier's meaning, how to read it and whether it was verified. Reads never touch
    /// the game (system RAM and cartridge RAM only, no registers).
    /// </summary>
    public sealed class AutoTasVariablesPanel : Form
    {
        private const int WideWidth = 270, NarrowWidth = 44;
        private readonly MainForm form;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
        private readonly Label header = new() { Name = "autotasVarsHeader", Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Padding = new Padding(6, 4, 6, 0), Text = "Variables" };
        private readonly Button collapse = new() { Name = "autotasVarsCollapse", AccessibleName = "autotasVarsCollapse", Text = "<", Width = 30, Height = 24 };
        private readonly CheckBox all = new() { Name = "autotasVarsAll", Text = "All", AutoSize = true, Margin = new Padding(6, 5, 0, 0) };
        private readonly ListView list = new() { Name = "autotasVars", AccessibleName = "autotasVars", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, ShowItemToolTips = true, Font = new Font("Consolas", 10f) };
        private readonly Label note = new() { Name = "autotasVarsNote", Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(6, 2, 6, 2), ForeColor = SystemColors.GrayText };
        private readonly FlowLayoutPanel bar = new() { Dock = DockStyle.Top, Height = 30, Padding = new Padding(3, 2, 3, 0), WrapContents = false };
        private List<AutoTasVariableView> vars = new();
        private string[] last = Array.Empty<string>();
        private bool collapsed;

        public AutoTasVariablesPanel(MainForm form)
        {
            this.form = form;
            Text = "AutoTAS variables";
            Name = "AutoTasVariablesPanel";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimumSize = new Size(NarrowWidth, 200);
            Width = WideWidth;

            list.Columns.Add("Name", 112);
            list.Columns.Add("Value", 140);
            bar.Controls.AddRange(new Control[] { collapse, all });
            Controls.Add(list);
            Controls.Add(note);
            Controls.Add(bar);
            Controls.Add(header);

            collapse.Click += (_, _) => SetCollapsed(!collapsed);
            all.CheckedChanged += (_, _) => Reload();
            form.Move += Snap; form.Resize += Snap;
            Shown += (_, _) => { Reload(); Snap(null, EventArgs.Empty); };
            timer.Tick += (_, _) => Tick();
            timer.Start();
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); form.Move -= Snap; form.Resize -= Snap; };
        }

        internal bool Collapsed => collapsed;
        internal void SetShowAll(bool on) => all.Checked = on;
        internal int Count => list.Items.Count;

        internal void SetCollapsed(bool c)
        {
            collapsed = c;
            collapse.Text = c ? ">" : "<";
            all.Visible = list.Visible = note.Visible = !c;
            header.Text = c ? "" : "Variables";
            MinimumSize = new Size(NarrowWidth, 200);
            Width = c ? NarrowWidth : WideWidth;
            Snap(null, EventArgs.Empty);
        }

        /// <summary>Against the main window's left edge (inside the screen when there is no room).</summary>
        private void Snap(object? sender, EventArgs e)
        {
            if (form.WindowState == FormWindowState.Minimized) return;
            var area = Screen.FromControl(form).WorkingArea;
            int x = form.Left - Width; if (x < area.Left) x = Math.Min(area.Right - Width, area.Left);
            Location = new Point(x, form.Top);
            Height = Math.Max(MinimumSize.Height, form.Height);
        }

        /// <summary>Reads the running package's variables.json again.</summary>
        internal void Reload()
        {
            var p = form.AutoTasPackageNow;
            string why = "";
            vars = p != null ? AutoTasVariables.Load(p, out why) : new();
            if (p == null) why = "no AutoTAS package";
            var shown = all.Checked ? vars : vars.Where(v => v.Var.Priority <= 2).ToList();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var v in shown)
            {
                var it = new ListViewItem(v.Var.Name) { Tag = v, ToolTipText = $"{v.Var.Address} ({v.Var.Size} byte{(v.Var.Size == 1 ? "" : "s")}): {v.Var.Meaning}\nread: {v.Var.Read}\n{(v.Var.Verified ? "verified" : "NOT verified")}, priority {v.Var.Priority}" };
                if (v.Var.Priority == 1) it.Font = new Font(list.Font, FontStyle.Bold);
                if (!v.Var.Verified) it.ForeColor = SystemColors.GrayText;
                it.SubItems.Add("");
                list.Items.Add(it);
            }
            list.EndUpdate();
            last = new string[list.Items.Count];
            note.Text = why.Length > 0 ? why : $"{vars.Count} variables from the game dossier; grey = not verified; hover for meaning";
            Tick();
        }

        /// <summary>Updates the values that changed.</summary>
        private void Tick()
        {
            if (collapsed || !Visible || list.Items.Count == 0) return;
            Func<int, int>? peek = form.AutoTasPeeker();
            if (peek == null) { note.Text = "variables are read on NES games for now"; return; }
            for (int i = 0; i < list.Items.Count; i++)
            {
                var v = (AutoTasVariableView)list.Items[i].Tag!;
                string s;
                try { s = v.Format(peek); } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or NullReferenceException) { s = "?"; }
                if (s != last[i]) { last[i] = s; list.Items[i].SubItems[1].Text = s; }
            }
        }

        /// <summary>The values as shown (for the HTTP status and tests).</summary>
        internal object Snapshot() => list.Items.Cast<ListViewItem>().Select(it => (object)new { name = it.Text, value = it.SubItems[1].Text, priority = ((AutoTasVariableView)it.Tag!).Var.Priority }).ToArray();
    }
}
