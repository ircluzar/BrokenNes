using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BrokenNes.Windows.Synth
{
    /// <summary>
    /// "Which FL Studio?": every installation found, with what each holds now, to tick one or several; Browse... adds one that is
    /// somewhere unusual. Nothing is installed here: the caller gets the ticked installations.
    /// </summary>
    internal sealed class InstallVstDialog : Form
    {
        private const int BCM_SETSHIELD = 0x160C;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private sealed class Row
        {
            public required FlStudioInstall Install;
            public required string Status;
            public bool Running;
            public override string ToString() =>
                $"{Install.Name}{(Install.Certified ? "" : "  (not certified)")}   -   {Status}{(Running ? "   -   FL Studio is running" : "")}   -   {Install.Root}";
        }

        private readonly string dll;
        private readonly CheckedListBox list = new() { CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
        private readonly Button install = new() { Text = "Install", FlatStyle = FlatStyle.System, DialogResult = DialogResult.None, Width = 110, Height = 32 };
        private readonly Button browse = new() { Text = "Browse...", Width = 100, Height = 32 };
        private readonly Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };

        /// <summary>The ticked installations, once the dialog has been confirmed with Install.</summary>
        public IReadOnlyList<FlStudioInstall> Selected { get; private set; } = Array.Empty<FlStudioInstall>();

        public InstallVstDialog(string pluginDll, IEnumerable<FlStudioInstall> found)
        {
            dll = pluginDll;
            Text = "Install BrokenNes 2 to FL Studio";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(760, 420);
            Font = SystemFonts.MessageBoxFont ?? Font;
            AcceptButton = install;
            CancelButton = cancel;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(730, 0), Margin = new Padding(0, 0, 0, 8),
                Text = "Tick the FL Studio installation(s) to add \"Bogue :: BrokenNes 2\" to. It is copied into that FL Studio's own plugin folder, " +
                       "which is under Program Files: Windows will ask for permission once. Close FL Studio first; it only looks for plugins when it starts.",
            }, 0, 0);

            list.Dock = DockStyle.Fill;
            list.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(list, 0, 1);

            root.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(730, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 8),
                Text = $"Only FL Studio {FlStudioInstall.CertifiedEdition} has been tested with this plugin. The others may work, but nobody has checked, and a plugin that fails to load can take FL Studio down with it.",
            }, 0, 2);

            var plugin = new System.IO.FileInfo(dll);
            root.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(730, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 10),
                Text = $"Plugin to install: {plugin.FullName}  ({plugin.Length / 1048576.0:0.0} MB, built {plugin.LastWriteTime:yyyy-MM-dd HH:mm})",
            }, 0, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(install);
            buttons.Controls.Add(browse);
            root.Controls.Add(buttons, 0, 4);
            Controls.Add(root);

            foreach (var f in found) AddRow(f, tick: false);
            // the newest certified installation is the one most people want; with none certified, the newest there is
            var first = list.Items.Cast<Row>().FirstOrDefault(r => r.Install.Certified) ?? list.Items.Cast<Row>().FirstOrDefault();
            if (first != null) list.SetItemChecked(list.Items.IndexOf(first), true);
            if (list.Items.Count == 0) ShowNoneFound();

            if (!PluginInstaller.IsAdmin()) SendMessage(install.Handle, BCM_SETSHIELD, IntPtr.Zero, (IntPtr)1);

            browse.Click += (s, e) => BrowseForFl();
            install.Click += (s, e) => Confirm();
        }

        /// <summary>The list holds one greyed line saying so, until Browse... adds a real row (AddRow clears it).</summary>
        private void ShowNoneFound()
        {
            list.Items.Add("No FL Studio was found in the usual places: use Browse... to point at yours.");
            list.Enabled = false;
        }

        private void AddRow(FlStudioInstall install, bool tick)
        {
            if (!list.Enabled) { list.Items.Clear(); list.Enabled = true; }
            var existing = list.Items.Cast<Row>().FirstOrDefault(r => string.Equals(r.Install.Root, install.Root, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { if (tick) list.SetItemChecked(list.Items.IndexOf(existing), true); return; }
            int i = list.Items.Add(new Row { Install = install, Status = PluginInstaller.StatusOf(install, dll), Running = FlStudioLocator.IsRunning(install) });
            if (tick) list.SetItemChecked(i, true);
        }

        private void BrowseForFl()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Pick the FL Studio folder (the one that contains FL64.exe and a Plugins folder).",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var found = FlStudioLocator.FromFolder(dlg.SelectedPath);
            if (found == null)
            {
                MessageBox.Show(this, $"That folder does not look like an FL Studio installation (no FL64.exe, no Plugins\\Fruity folder):\n\n{dlg.SelectedPath}",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            AddRow(found, tick: true);
        }

        private void Confirm()
        {
            var ticked = list.CheckedItems.Cast<object>().OfType<Row>().Select(r => r.Install).ToList();
            if (ticked.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one FL Studio installation.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Selected = ticked;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
