using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BrokenNes.CorruptorModels;

namespace BrokenNes.Windows.Tools
{
    internal sealed class HexEditorForm : Form
    {
        private readonly MainForm main;
        private readonly ComboBox domainCombo;
        private readonly NumericUpDown addressUpDown;
        private readonly NumericUpDown rowsUpDown;
        private readonly DxHexGridControl hexView;
        private readonly Label statusLabel;
        private readonly Button prevButton;
        private readonly Button nextButton;
        private readonly Button refreshButton;
        private readonly CheckBox autoRefreshCheck;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private IList<DomainSel> domains = new List<DomainSel>();
        private const int BytesPerRow = 16;
        private int currentBaseAddress;
        private bool isRefreshing;
        private byte[] currentData = Array.Empty<byte>();
        private DomainSel? currentDomain;

        public HexEditorForm(MainForm main)
        {
            this.main = main;
            Text = "Hex Editor";
            Width = 960;
            Height = 720;
            StartPosition = FormStartPosition.CenterParent;

            domainCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            domainCombo.SelectedIndexChanged += async (_, __) => await OnDomainChangedAsync();

            addressUpDown = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 0xFFFFFF, Increment = BytesPerRow, Hexadecimal = true };
            addressUpDown.ValueChanged += async (_, __) => await RefreshViewAsync();

            rowsUpDown = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 8, Maximum = 128, Increment = 8, Value = 32 };
            rowsUpDown.ValueChanged += async (_, __) => await RefreshViewAsync();

            prevButton = new Button { Text = "◀ Page", AutoSize = true };
            prevButton.Click += async (_, __) => await MovePageAsync(-1);

            nextButton = new Button { Text = "Page ▶", AutoSize = true };
            nextButton.Click += async (_, __) => await MovePageAsync(1);

            refreshButton = new Button { Text = "Refresh", AutoSize = true };
            refreshButton.Click += async (_, __) => await RefreshViewAsync();

            autoRefreshCheck = new CheckBox { Text = "Auto-refresh", AutoSize = true, Checked = true };
            autoRefreshCheck.CheckedChanged += (_, __) => refreshTimer.Enabled = autoRefreshCheck.Checked;

            refreshTimer = new System.Windows.Forms.Timer { Interval = 500, Enabled = autoRefreshCheck.Checked };
            refreshTimer.Tick += async (_, __) => await RefreshViewAsync();

            hexView = new DxHexGridControl { Dock = DockStyle.Fill };
            hexView.CellClicked += async addr => await EditCellAsync(addr);
            statusLabel = new Label { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4), Text = "Load a ROM to inspect memory." };

            Controls.Add(BuildLayout());

            main.CorruptorStateChanged += OnCorruptorStateChanged;
            FormClosed += (_, __) =>
            {
                refreshTimer.Dispose();
                main.CorruptorStateChanged -= OnCorruptorStateChanged;
            };
            Shown += async (_, __) => await LoadDomainsAsync();
        }

        private Control BuildLayout()
        {
            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 10, Padding = new Padding(8) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            top.Controls.Add(new Label { Text = "Domain:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            top.Controls.Add(domainCombo, 1, 0);
            top.Controls.Add(new Label { Text = "Address:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 0, 0, 0) }, 2, 0);
            top.Controls.Add(addressUpDown, 3, 0);
            top.Controls.Add(new Label { Text = "Rows:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 0, 0, 0) }, 4, 0);
            top.Controls.Add(rowsUpDown, 5, 0);
            top.Controls.Add(prevButton, 6, 0);
            top.Controls.Add(nextButton, 7, 0);
            top.Controls.Add(refreshButton, 8, 0);
            top.Controls.Add(autoRefreshCheck, 9, 0);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 28, ColumnCount = 1, Padding = new Padding(8, 2, 8, 6) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.Controls.Add(statusLabel, 0, 0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            root.Controls.Add(top, 0, 0);
            root.Controls.Add(hexView, 0, 1);
            root.Controls.Add(bottom, 0, 2);
            return root;
        }

        private void OnCorruptorStateChanged()
        {
            if (IsDisposed) return;
            BeginInvoke(async () => await LoadDomainsAsync(true));
        }

        private async Task LoadDomainsAsync(bool keepSelection = false)
        {
            var snapshot = main.GetCorruptorSnapshot();
            var currentKey = keepSelection ? SelectedDomainKey : null;

            domains = snapshot?.MemoryDomains?.ToList() ?? new List<DomainSel>();
            domainCombo.Items.Clear();

            foreach (var domain in domains)
            {
                domainCombo.Items.Add(new DomainOption(domain));
            }

            if (domainCombo.Items.Count == 0)
            {
                statusLabel.Text = "Load a ROM to inspect memory.";
                hexView.UpdateData(string.Empty, 0, 0, Array.Empty<byte>());
                return;
            }

            int index = -1;
            if (!string.IsNullOrWhiteSpace(currentKey))
            {
                index = domainCombo.Items.Cast<DomainOption>().ToList().FindIndex(i => string.Equals(i.Key, currentKey, StringComparison.OrdinalIgnoreCase));
            }

            domainCombo.SelectedIndex = index >= 0 ? index : 0;
            UpdateDomainLimits();
            await RefreshViewAsync();
        }

        private async Task OnDomainChangedAsync()
        {
            UpdateDomainLimits();
            await RefreshViewAsync();
        }

        private void UpdateDomainLimits()
        {
            var domain = SelectedDomain;
            int maxStart = Math.Max(0, (domain?.Size ?? 1) - 1);
            addressUpDown.Maximum = Math.Max(0, maxStart);
            if (addressUpDown.Value > addressUpDown.Maximum)
            {
                addressUpDown.Value = addressUpDown.Maximum;
            }
        }

        private DomainSel? SelectedDomain => (domainCombo.SelectedItem as DomainOption)?.Domain;

        private string? SelectedDomainKey => SelectedDomain?.Key;

        private int PageSize => BytesPerRow * (int)rowsUpDown.Value;

        private async Task MovePageAsync(int direction)
        {
            var domain = SelectedDomain;
            if (domain == null) return;

            int newStart = currentBaseAddress + (direction * PageSize);
            newStart = Math.Max(0, newStart);
            int maxStart = Math.Max(0, domain.Size - PageSize);
            newStart = Math.Min(newStart, maxStart);

            addressUpDown.Value = Math.Min(addressUpDown.Maximum, newStart);
            await RefreshViewAsync();
        }

        private async Task RefreshViewAsync()
        {
            if (isRefreshing) return;
            var domain = SelectedDomain;
            if (domain == null)
            {
                statusLabel.Text = "Load a ROM to inspect memory.";
                hexView.UpdateData(string.Empty, 0, 0, Array.Empty<byte>());
                return;
            }

            int start = (int)addressUpDown.Value;
            int length = Math.Min(PageSize, Math.Max(0, domain.Size - start));

            if (length <= 0)
            {
                hexView.UpdateData(domain.Label, start, domain.Size, Array.Empty<byte>());
                statusLabel.Text = "Address is past the end of this domain.";
                return;
            }

            isRefreshing = true;
            try
            {
                var data = await main.ReadMemoryAsync(domain.Key, start, length);
                currentBaseAddress = start;
                currentData = data;
                currentDomain = domain;
                hexView.UpdateData(domain.Label, start, domain.Size, data, BytesPerRow);
                statusLabel.Text = $"Domain: {domain.Label} ({domain.Size} bytes) • Showing {Math.Min(data.Length, domain.Size - start)} bytes @ 0x{start:X}";
            }
            finally
            {
                isRefreshing = false;
            }
        }

        private async Task EditCellAsync(int absoluteAddress)
        {
            var domain = currentDomain;
            if (domain == null) return;
            int offset = absoluteAddress - currentBaseAddress;
            byte currentVal = (offset >= 0 && offset < currentData.Length) ? currentData[offset] : (byte)0;

            using var dialog = new Form
            {
                Text = $"Edit 0x{absoluteAddress:X6}",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(260, 110)
            };

            var label = new Label { Text = "New value (hex):", AutoSize = true, Location = new Point(12, 15) };
            var box = new TextBox { Location = new Point(15, 40), Width = 80, Text = currentVal.ToString("X2") };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(120, 70), Width = 60 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(185, 70), Width = 60 };
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            dialog.Controls.AddRange(new Control[] { label, box, ok, cancel });

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            string text = box.Text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
            }

            if (!byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                await RefreshViewAsync();
                return;
            }

            await main.WriteMemoryAsync(domain.Key, absoluteAddress, value);
            await RefreshViewAsync();
        }

        private sealed class DomainOption
        {
            public DomainOption(DomainSel domain) => Domain = domain;
            public DomainSel Domain { get; }
            public string Key => Domain.Key;
            public override string ToString() => $"{Domain.Label} ({Domain.Size} bytes)";
        }
    }
}
