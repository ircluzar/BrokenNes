using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using BrokenNes.CorruptorModels;

namespace BrokenNes.Windows.Tools
{
    internal sealed class RealTimeCorruptorForm : Form
    {
        private readonly MainForm main;
        private NumericUpDown intensityUpDown;
        private ComboBox blastTypeCombo;
        private CheckedListBox domainsList;
        private CheckBox autoCorruptChk;
        private Label lastInfoLabel;
        private ComboBox crashBehaviorCombo;
        private CheckBox stubbornChk;
        private bool isInitializing = true;
        private ToolTip toolTip;

        public RealTimeCorruptorForm(MainForm main)
        {
            this.main = main;
            Text = "Real-Time Corruptor";
            Width = 580;
            Height = 650;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            
            // Dark Theme Base
            BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            ForeColor = System.Drawing.Color.White;

            InitializeUi();
            main.CorruptorStateChanged += OnStateChanged;
            FormClosed += (_, __) => main.CorruptorStateChanged -= OnStateChanged;
            
            // Set initial defaults without triggering event handlers
            blastTypeCombo.SelectedIndex = 0; // "RANDOM"
            crashBehaviorCombo.SelectedIndex = 1; // "IgnoreErrors"
            isInitializing = false;
            
            // Request initial refresh after form is shown
            Shown += (_, __) => BeginInvoke(new Action(() => OnStateChanged()));
        }

        private void InitializeUi()
        {
            toolTip = new ToolTip();
            toolTip.AutoPopDelay = 5000;
            toolTip.InitialDelay = 500;
            toolTip.ReshowDelay = 500;
            toolTip.ShowAlways = true;

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10),
                AutoSize = true // Allow it to perform layout
            };
            
            // Group 1: Corruption Settings
            var grpSettings = CreateGroupBox("Corruption Settings", 200);
            var settingsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(8) };
            settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

            settingsLayout.Controls.Add(CreateLabel("Intensity:"), 0, 0);
            intensityUpDown = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 1, Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(60, 60, 60), ForeColor = System.Drawing.Color.White };
            intensityUpDown.ValueChanged += (_, __) => { if (!isInitializing) main.SetCorruptIntensity((int)intensityUpDown.Value); };
            toolTip.SetToolTip(intensityUpDown, "Determines the number of corruption operations performed in a single blast.");
            settingsLayout.Controls.Add(intensityUpDown, 1, 0);

            settingsLayout.Controls.Add(CreateLabel("Blast Type:"), 0, 1);
            blastTypeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(60, 60, 60), ForeColor = System.Drawing.Color.White, FlatStyle = FlatStyle.Flat };
            blastTypeCombo.Items.AddRange(new object[] { "RANDOM", "TILT", "RANDOMTILT", "NOP", "BITFLIP", "IMAGINENEXT", "IMAGINERANDOM" });
            blastTypeCombo.SelectedIndexChanged += (_, __) => { if (!isInitializing) main.SetBlastType(blastTypeCombo.SelectedItem?.ToString() ?? "RANDOM"); };
            toolTip.SetToolTip(blastTypeCombo, "Selects the algorithm used to corrupt memory.\nRANDOM: Random byte replacement.\nTILT: Increments/decrements bytes.\nBITFLIP: Flips random bits.");
            settingsLayout.Controls.Add(blastTypeCombo, 1, 1);

            settingsLayout.Controls.Add(CreateLabel("Memory Domains:"), 0, 2);
            var domainsPanel = new Panel { Dock = DockStyle.Fill, Height = 120, MinimumSize = new System.Drawing.Size(0, 120) };
            domainsList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BackColor = System.Drawing.Color.FromArgb(60, 60, 60), ForeColor = System.Drawing.Color.White, BorderStyle = BorderStyle.None };
            domainsList.ItemCheck += (_, __) => { if (!isInitializing) BeginInvoke(new Action(DomainsChanged)); };
            toolTip.SetToolTip(domainsList, "Select which memory regions (RAM, VRAM, etc.) are targeted by the corruptor.");
            domainsPanel.Controls.Add(domainsList);
            
            var refreshBtn = CreateButton("Refresh");
            refreshBtn.Dock = DockStyle.Bottom;
            refreshBtn.Click += (_, __) => main.RefreshMemoryDomainsRequested();
            toolTip.SetToolTip(refreshBtn, "Reloads the list of available memory domains from the emulator.");
            domainsPanel.Controls.Add(refreshBtn);
            
            settingsLayout.Controls.Add(domainsPanel, 1, 2);
            grpSettings.Controls.Add(settingsLayout);
            mainLayout.Controls.Add(grpSettings);


            // Group 2: Behavior
            var grpBehavior = CreateGroupBox("Behavior", 140);
            var behaviorLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(8) };
            behaviorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            behaviorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

            behaviorLayout.Controls.Add(CreateLabel("Crash Behavior:"), 0, 0);
            crashBehaviorCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(60, 60, 60), ForeColor = System.Drawing.Color.White, FlatStyle = FlatStyle.Flat };
            crashBehaviorCombo.Items.AddRange(new object[] { "RedScreen", "IgnoreErrors", "ImagineFix" });
            crashBehaviorCombo.SelectedIndexChanged += (_, __) => { if (!isInitializing) main.SetCrashBehaviorFromTools(crashBehaviorCombo.SelectedItem?.ToString() ?? "RedScreen"); };
            toolTip.SetToolTip(crashBehaviorCombo, "Determines what happens when the emulator crashes.\nRedScreen: Stops emulation.\nIgnoreErrors: Attempts to continue.\nImagineFix: Uses ML to try and repair the state.");
            behaviorLayout.Controls.Add(crashBehaviorCombo, 1, 0);

            stubbornChk = CreateCheckBox("Stubborn Imagine Fix");
            stubbornChk.CheckedChanged += (_, __) => { if (!isInitializing) main.SetStubbornMode(stubbornChk.Checked); };
            toolTip.SetToolTip(stubbornChk, "If enabled, ML Imagine Fix will retry repeatedly until the game runs without crashing.");
            behaviorLayout.Controls.Add(stubbornChk, 0, 1);
            behaviorLayout.SetColumnSpan(stubbornChk, 2);

            autoCorruptChk = CreateCheckBox("Auto-corrupt each frame");
            autoCorruptChk.CheckedChanged += (_, __) => { if (!isInitializing) main.SetAutoCorrupt(autoCorruptChk.Checked); };
            toolTip.SetToolTip(autoCorruptChk, "Typically used with 'Lets it rip', this applies corruption every frame.");
            behaviorLayout.Controls.Add(autoCorruptChk, 0, 2);
            behaviorLayout.SetColumnSpan(autoCorruptChk, 2);
            
            grpBehavior.Controls.Add(behaviorLayout);
            mainLayout.Controls.Add(grpBehavior);


            // Group 3: Actions
            var grpActions = CreateGroupBox("Actions", 90);
            var actionFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
            
            var blastBtn = CreateButton("Blast Now");
            blastBtn.MinimumSize = new System.Drawing.Size(120, 32);
            blastBtn.Click += (_, __) => main.RequestBlast();
            toolTip.SetToolTip(blastBtn, "Trigger a single corruption blast immediately.");
            actionFlow.Controls.Add(blastBtn);

            var autoCorruptBtn = CreateButton("Auto-Corrupt");
            autoCorruptBtn.MinimumSize = new System.Drawing.Size(120, 32);
            autoCorruptBtn.Click += (_, __) => main.SetAutoCorrupt(!autoCorruptChk.Checked);
            toolTip.SetToolTip(autoCorruptBtn, "Toggle auto-corruption on/off. When enabled, corruption is applied each frame.");
            actionFlow.Controls.Add(autoCorruptBtn);

            var letItRipBtn = CreateButton("Let it rip");
            letItRipBtn.MinimumSize = new System.Drawing.Size(120, 32);
            letItRipBtn.Click += (_, __) => main.RequestLetItRip();
            toolTip.SetToolTip(letItRipBtn, "Preset: Sets intensity=1, selects PRG ROM + System RAM, and enables auto-corrupt.");
            actionFlow.Controls.Add(letItRipBtn);

            grpActions.Controls.Add(actionFlow);
            mainLayout.Controls.Add(grpActions);

            // Status
            lastInfoLabel = CreateLabel("Last: -");
            lastInfoLabel.Dock = DockStyle.Bottom;
            lastInfoLabel.Padding = new Padding(5, 8, 5, 8);
            lastInfoLabel.ForeColor = System.Drawing.Color.LightGreen;
            mainLayout.Controls.Add(lastInfoLabel);

            Controls.Add(mainLayout);
        }

        private GroupBox CreateGroupBox(string text, int height)
        {
            return new GroupBox
            {
                Text = text,
                Dock = DockStyle.Top,
                Height = height,
                MinimumSize = new System.Drawing.Size(0, height),
                ForeColor = System.Drawing.Color.Cyan, // Accent color for headers
                Padding = new Padding(12),
                Margin = new Padding(0, 0, 0, 8)
            };
        }

        private Label CreateLabel(string text)
        {
            return new Label { Text = text, Anchor = AnchorStyles.Left, AutoSize = true, ForeColor = System.Drawing.Color.White };
        }

        private CheckBox CreateCheckBox(string text)
        {
            return new CheckBox { Text = text, Dock = DockStyle.Fill, ForeColor = System.Drawing.Color.White };
        }

        private Button CreateButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = System.Drawing.Color.FromArgb(60, 60, 60),
                ForeColor = System.Drawing.Color.White
            };
            btn.FlatAppearance.BorderColor = System.Drawing.Color.Gray;
            return btn;
        }

        private void DomainsChanged()
        {
            var keys = domainsList.CheckedItems.Cast<DomainItem>().Select(d => d.Key).ToList();
            main.SetSelectedDomains(keys);
        }

        private void OnStateChanged()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshUi)); return; }
            RefreshUi();
        }

        private void RefreshUi()
        {
            if (!main.IsEmulatorReady) return;
            var snapshot = main.GetCorruptorSnapshot();
            if (snapshot == null) return; // Lock unavailable, skip this refresh
            
            // Suppress event handlers while updating values
            isInitializing = true;
            try
            {
                intensityUpDown.Value = Math.Min(Math.Max(snapshot.CorruptIntensity, 1), 65535);
                blastTypeCombo.SelectedItem = blastTypeCombo.Items.Cast<object>().FirstOrDefault(i => string.Equals(i.ToString(), snapshot.BlastType, StringComparison.OrdinalIgnoreCase)) ?? "RANDOM";

                domainsList.Items.Clear();
                foreach (var d in snapshot.MemoryDomains)
                {
                    var item = new DomainItem(d.Key, $"{d.Label} ({d.Size})");
                    domainsList.Items.Add(item, d.Selected);
                }

                autoCorruptChk.Checked = snapshot.AutoCorrupt;
                lastInfoLabel.Text = $"Last: {snapshot.LastBlastInfo}";
                stubbornChk.Checked = snapshot.StubbornMode;

                // Crash behavior
                crashBehaviorCombo.SelectedItem = crashBehaviorCombo.Items.Cast<object>().FirstOrDefault(o => string.Equals(o.ToString(), snapshot.CrashBehavior, StringComparison.OrdinalIgnoreCase)) ?? "RedScreen";
            }
            finally
            {
                isInitializing = false;
            }
        }

        private sealed record DomainItem(string Key, string Label)
        {
            public override string ToString() => Label;
        }
    }
}
