using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BrokenNes.Windows.AutoTas;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// Tools &amp; Activities > AutoTAS (docs/projects/autotas-plan.md section 5): a side panel that sits against the main window's right edge and
    /// follows it. Header: the game, the TAS version, whether the cores match. Controls: Play TAS from here, Pause, speed, Take control, Return.
    /// The chapter list (thumbnail, title, kind, length, score): Jump (clean: a fresh arrival, the default), Jump (raw: the run as it was), Watch
    /// from here (jump and play); a double-click jumps. The selected chapter's notes show under the list. Nothing is saved.
    /// </summary>
    public sealed class AutoTasPanel : Form
    {
        private readonly MainForm form;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 400 };
        private readonly Label header = new() { Name = "autotasHeader", Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(6, 4, 6, 0) };
        private readonly Label core = new() { Name = "autotasCore", Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 0, 6, 0) };
        private readonly Button play = new() { Name = "autotasPlay", AccessibleName = "autotasPlay", Text = "Play TAS from here", Width = 128, Height = 28 };
        private readonly Button pause = new() { Name = "autotasPause", AccessibleName = "autotasPause", Text = "Pause", Width = 60, Height = 28 };
        private readonly ComboBox speed = new() { Name = "autotasSpeed", AccessibleName = "autotasSpeed", DropDownStyle = ComboBoxStyle.DropDownList, Width = 58 };
        private readonly Button take = new() { Name = "autotasTakeControl", AccessibleName = "autotasTakeControl", Text = "Take control", Width = 94, Height = 28 };
        private readonly Button back = new() { Name = "autotasReturn", AccessibleName = "autotasReturn", Text = "Return", Width = 64, Height = 28 };
        private readonly ListView list = new() { Name = "autotasChapters", AccessibleName = "autotasChapters", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        private readonly ImageList thumbs = new() { ImageSize = new Size(64, 60), ColorDepth = ColorDepth.Depth24Bit };
        private readonly Button jumpClean = new() { Name = "autotasJumpClean", AccessibleName = "autotasJumpClean", Text = "Jump", Width = 64, Height = 28 };
        private readonly Button jumpRaw = new() { Name = "autotasJumpRaw", AccessibleName = "autotasJumpRaw", Text = "Jump (raw)", Width = 80, Height = 28 };
        private readonly Button watch = new() { Name = "autotasWatch", AccessibleName = "autotasWatch", Text = "Watch from here", Width = 112, Height = 28 };
        private readonly CheckBox showNotes = new() { Name = "autotasShowNotes", Text = "Notes", AutoSize = true, Checked = true, Margin = new Padding(6, 7, 0, 0) };
        private readonly TextBox notes = new() { Name = "autotasNotes", Dock = DockStyle.Bottom, Height = 96, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 8.5f) };
        private readonly Label status = new() { Name = "autotasStatus", Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(6, 2, 6, 2) };
        private AutoTasPackage? shown;
        private string stateKey = "";

        public AutoTasPanel(MainForm form)
        {
            this.form = form;
            Text = "AutoTAS";
            Name = "AutoTasPanel";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimumSize = new Size(380, 360);
            Size = new Size(450, Math.Max(520, form.Height));

            speed.Items.AddRange(new object[] { "1x", "2x", "4x", "max" });
            speed.SelectedIndex = SpeedIndex(form.AutoTasSpeed);

            list.Columns.Add("Chapter", 170);
            list.Columns.Add("Kind", 56);
            list.Columns.Add("Length", 64);
            list.Columns.Add("Score", 46);
            list.SmallImageList = thumbs;

            var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(3, 3, 3, 0), WrapContents = false };
            controls.Controls.AddRange(new Control[] { play, pause, speed, take, back });
            var chapterRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(3, 3, 3, 0), WrapContents = false };
            chapterRow.Controls.AddRange(new Control[] { jumpClean, jumpRaw, watch, showNotes });
            speed.Margin = new Padding(3, 5, 3, 0);

            Controls.Add(list);
            Controls.Add(chapterRow);
            Controls.Add(notes);
            Controls.Add(status);
            Controls.Add(controls);
            Controls.Add(core);
            Controls.Add(header);

            play.Click += (_, _) => Say(form.AutoTasPlay());
            pause.Click += (_, _) => Say(form.AutoTasPause());
            take.Click += async (_, _) => Say(await form.AutoTasTakeControlAsync());
            back.Click += async (_, _) => Say(await form.AutoTasReturnAsync());
            speed.SelectedIndexChanged += (_, _) => Say(form.AutoTasSetSpeed(speed.SelectedIndex switch { 1 => 2f, 2 => 4f, 3 => 16f, _ => 1f }));
            jumpClean.Click += async (_, _) => { if (Selected() is int i) Say(await form.AutoTasJumpAsync(i, true)); };
            jumpRaw.Click += async (_, _) => { if (Selected() is int i) Say(await form.AutoTasJumpAsync(i, false)); };
            watch.Click += async (_, _) => { if (Selected() is int i) { Say(await form.AutoTasJumpAsync(i, true)); if (form.AutoTasCurrent == i) Say(form.AutoTasPlay()); } };
            list.DoubleClick += async (_, _) => { if (Selected() is int i) Say(await form.AutoTasJumpAsync(i, true)); };
            list.SelectedIndexChanged += (_, _) => ShowNotes();
            showNotes.CheckedChanged += (_, _) => { notes.Visible = showNotes.Checked; };

            form.Move += Snap; form.Resize += Snap;
            Shown += (_, _) => { Snap(null, EventArgs.Empty); Reload(); };
            timer.Tick += (_, _) => RefreshState();
            timer.Start();
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); form.Move -= Snap; form.Resize -= Snap; foreach (Image im in thumbs.Images) im.Dispose(); };
        }

        private static int SpeedIndex(float s) => s >= 16 ? 3 : s >= 4 ? 2 : s >= 2 ? 1 : 0;

        /// <summary>The panel sits against the main window's right edge (or inside the screen when there is no room).</summary>
        private void Snap(object? sender, EventArgs e)
        {
            if (form.WindowState == FormWindowState.Minimized) return;
            var area = Screen.FromControl(form).WorkingArea;
            int x = form.Right; if (x + Width > area.Right) x = Math.Max(area.Left, area.Right - Width);
            Location = new Point(x, form.Top);
            Height = Math.Max(MinimumSize.Height, form.Height);
        }

        private int? Selected() => list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : null;

        private void Say(string msg) { status.Text = msg; RefreshState(); }

        /// <summary>Fills the chapter list from the running game's package.</summary>
        internal void Reload()
        {
            var p = form.AutoTasPackageNow;
            shown = p;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Image im in thumbs.Images) im.Dispose();
            thumbs.Images.Clear();
            if (p != null)
            {
                header.Text = $"{form.AtGameTitle()}  -  TAS v{p.Manifest.Version}";
                for (int i = 0; i < p.Manifest.Chapters.Count; i++)
                {
                    var c = p.Manifest.Chapters[i];
                    int img = -1;
                    string? t = p.ThumbOf(c);
                    if (t != null)
                    {
                        try { using var src = Image.FromStream(new MemoryStream(File.ReadAllBytes(t))); thumbs.Images.Add(new Bitmap(src, thumbs.ImageSize)); img = thumbs.Images.Count - 1; }
                        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException) { }
                    }
                    if (img < 0) { thumbs.Images.Add(Placeholder(c.Kind)); img = thumbs.Images.Count - 1; }
                    var it = new ListViewItem(c.Title.Length > 0 ? c.Title : c.Id, img) { Tag = i };
                    it.SubItems.Add(KindText(c.Kind));
                    it.SubItems.Add(Duration(c.Frames));
                    it.SubItems.Add(c.Quality?.Score ?? "-");
                    if (p.StateOf(c, false) == null && p.StateOf(c, true) == null) it.ForeColor = SystemColors.GrayText;
                    list.Items.Add(it);
                }
            }
            else header.Text = "AutoTAS: no package for this game";
            list.EndUpdate();
            stateKey = "";
            RefreshState();
            if (list.Items.Count > 0 && Selected() == null) list.Items[Math.Max(0, form.AutoTasCurrent)].Selected = true;
        }

        private static string KindText(string k) => k switch { "level" => "level", "boss" => "boss", "map" => "map", "area" => "area", _ => k };

        private static Bitmap Placeholder(string kind)
        {
            var b = new Bitmap(64, 60);
            using var g = Graphics.FromImage(b);
            var col = kind switch { "boss" => Color.FromArgb(150, 50, 50), "map" => Color.FromArgb(60, 110, 60), "area" => Color.FromArgb(70, 70, 120), _ => Color.FromArgb(70, 90, 130) };
            g.Clear(col);
            using var f = new Font("Segoe UI", 8f, FontStyle.Bold);
            var sz = g.MeasureString(kind, f);
            g.DrawString(kind, f, Brushes.White, (64 - sz.Width) / 2, (60 - sz.Height) / 2);
            return b;
        }

        private static string Duration(long frames)
        {
            double s = frames / 60.0988;
            return s >= 60 ? $"{(int)(s / 60)}:{(int)(s % 60):00}" : $"{s:0.0}s";
        }

        private void ShowNotes()
        {
            var p = shown;
            if (p == null || Selected() is not int i || i >= p.Manifest.Chapters.Count) { notes.Text = ""; return; }
            var c = p.Manifest.Chapters[i];
            string head = $"{c.Title} ({c.Id}): {Duration(c.Frames)}, deaths {c.Quality?.Deaths ?? 0}, score {c.Quality?.Score ?? "-"}, retries {c.Retries}{(c.AcceptedBy != null ? ", accepted by " + c.AcceptedBy : "")}";
            string body = "";
            string? n = p.NotesOf(c);
            if (n != null) { try { body = File.ReadAllText(n); } catch (IOException) { } }
            notes.Text = head + (body.Length > 0 ? "\r\n\r\n" + body.Replace("\r\n", "\n").Replace("\n", "\r\n") : "");
            jumpClean.Enabled = p.StateOf(c, true) != null || p.StateOf(c, false) != null;
            jumpRaw.Enabled = p.StateOf(c, false) != null;
            watch.Enabled = jumpClean.Enabled && p.SegOf(c, true) != null;
        }

        /// <summary>The buttons, the highlighted chapter and the core line (the timer and after every command).</summary>
        internal void RefreshState()
        {
            if (IsDisposed) return;
            if (!ReferenceEquals(shown, form.AutoTasPackageNow)) { Reload(); return; }
            int cur = form.AutoTasCurrent;
            string key = $"{cur}|{form.AutoTasPlaying}|{form.AutoTasCanReturn}";
            if (key != stateKey)
            {
                stateKey = key;
                for (int i = 0; i < list.Items.Count; i++)
                {
                    var it = list.Items[i];
                    bool on = i == cur;
                    it.Font = new Font(list.Font, on ? FontStyle.Bold : FontStyle.Regular);
                    it.BackColor = on ? Color.FromArgb(220, 240, 220) : list.BackColor;
                }
                if (cur >= 0 && cur < list.Items.Count) list.Items[cur].EnsureVisible();
            }
            int si = SpeedIndex(form.AutoTasSpeed);
            if (speed.SelectedIndex != si) speed.SelectedIndex = si;   // a change from the API shows here (the handler's set is a no-op)
            play.Enabled = shown != null && (cur >= 0 || form.AutoTasPlaying);
            play.Text = form.AutoTasPlaying ? "Playing..." : "Play TAS from here";
            take.Enabled = form.AutoTasPlaying;
            back.Enabled = form.AutoTasCanReturn;
            if (shown != null)
            {
                var (ok, text) = form.AutoTasCoreMatch();
                core.Text = text; core.ForeColor = ok ? Color.FromArgb(30, 130, 50) : Color.FromArgb(190, 110, 0);
            }
            if (status.Text.Length == 0 && form.AutoTasLast.Length > 0) status.Text = form.AutoTasLast;
            else if (form.AutoTasLast.Length > 0 && status.Text != form.AutoTasLast) status.Text = form.AutoTasLast;
        }
    }
}
