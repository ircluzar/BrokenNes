using System;
using System.Linq;
using System.Windows.Forms;

namespace BrokenNes.Windows.Tools
{
    internal sealed class ImagineForm : Form
    {
        private readonly MainForm main;
        private NumericUpDown epochUpDown;
        private Button loadBtn;
        private NumericUpDown bytesUpDown;
        private NumericUpDown tempUpDown;
        private NumericUpDown topKUpDown;
        private Button captureBtn;
        private Button predictBtn;
        private Button applyBtn;
        private Button bugBtn;
        private Label statusLabel;
        private TextBox snapshotBox;
        private TextBox predictedBox;

        private ToolTip toolTip;

        public ImagineForm(MainForm main)
        {
            this.main = main;
            Text = "Imagine (ML Byte Prediction)";
            Width = 900;
            Height = 700;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            // Dark Theme
            BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            ForeColor = System.Drawing.Color.White;

            InitializeUi();
        }

        private void InitializeUi()
        {
            toolTip = new ToolTip();
            toolTip.AutoPopDelay = 5000;
            toolTip.InitialDelay = 500;
            toolTip.ReshowDelay = 500;
            toolTip.ShowAlways = true;

            var root = new TableLayoutPanel 
            { 
                Dock = DockStyle.Fill, 
                ColumnCount = 2, 
                RowCount = 3, 
                Padding = new Padding(10) 
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

            // Group 1: Model Settings
            var grpSettings = CreateGroupBox("1. ML Model Settings");
            var settingsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(8) };
            settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            
            settingsLayout.Controls.Add(CreateLabel("Model Epoch:"), 0, 0);
            epochUpDown = CreateNumeric(1, 40, 30);
            toolTip.SetToolTip(epochUpDown, "Select the training epoch (version) of the model to use.");
            settingsLayout.Controls.Add(epochUpDown, 1, 0);

            loadBtn = CreateButton("Load Model");
            loadBtn.Click += (_, __) => LoadModel();
            toolTip.SetToolTip(loadBtn, "Load the selected AI model into memory.");
            settingsLayout.Controls.Add(loadBtn, 1, 1);

            settingsLayout.Controls.Add(CreateLabel("Bytes to Predict:"), 0, 2);
            bytesUpDown = CreateNumeric(1, 32, 2);
            bytesUpDown.ValueChanged += (_, __) => SetParams();
            toolTip.SetToolTip(bytesUpDown, "How many consecutive bytes the AI should generate.");
            settingsLayout.Controls.Add(bytesUpDown, 1, 2);

            settingsLayout.Controls.Add(CreateLabel("Temperature:"), 0, 3);
            tempUpDown = CreateNumeric(0, 150, 40, 2, 1);
            tempUpDown.ValueChanged += (_, __) => SetParams();
            toolTip.SetToolTip(tempUpDown, "Controls randomness. Higher values are more chaotic.");
            settingsLayout.Controls.Add(tempUpDown, 1, 3);
            
            settingsLayout.Controls.Add(CreateLabel("Top K:"), 0, 4);
            topKUpDown = CreateNumeric(0, 256, 1);
            topKUpDown.ValueChanged += (_, __) => SetParams();
            toolTip.SetToolTip(topKUpDown, "Limits predictions to the top K most likely bytes.");
            settingsLayout.Controls.Add(topKUpDown, 1, 4);

            grpSettings.Controls.Add(settingsLayout);
            root.Controls.Add(grpSettings, 0, 0);
            root.SetRowSpan(grpSettings, 2);


            // Group 2: Actions
            var grpActions = CreateGroupBox("2. Actions");
            var actionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(4) };
            actionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            
            captureBtn = CreateButton("A. Capture Snapshot"); 
            captureBtn.Dock = DockStyle.Fill;
            captureBtn.MinimumSize = new System.Drawing.Size(0, 36);
            captureBtn.Click += (_, __) => CaptureSnapshot();
            toolTip.SetToolTip(captureBtn, "Grab the current state of the emulator.");
            actionLayout.Controls.Add(captureBtn);

            predictBtn = CreateButton("B. Predict Bytes"); 
            predictBtn.Dock = DockStyle.Fill;
            predictBtn.MinimumSize = new System.Drawing.Size(0, 36);
            predictBtn.Click += (_, __) => Predict();
            toolTip.SetToolTip(predictBtn, "Ask the AI to predict what bytes should follow.");
            actionLayout.Controls.Add(predictBtn);

            applyBtn = CreateButton("C. Apply Patch"); 
            applyBtn.Dock = DockStyle.Fill;
            applyBtn.MinimumSize = new System.Drawing.Size(0, 36);
            applyBtn.Click += (_, __) => ApplyPatch();
            toolTip.SetToolTip(applyBtn, "Write the predicted bytes into the game memory.");
            actionLayout.Controls.Add(applyBtn);

            bugBtn = CreateButton("OR: Imagine a Bug (Auto)"); 
            bugBtn.Dock = DockStyle.Fill;
            bugBtn.MinimumSize = new System.Drawing.Size(0, 40);
            bugBtn.BackColor = System.Drawing.Color.DarkSlateBlue;
            bugBtn.Click += (_, __) => ImagineBug();
            toolTip.SetToolTip(bugBtn, "Automatically Capture, Predict, and Apply in one go.");
            actionLayout.Controls.Add(bugBtn);

            grpActions.Controls.Add(actionLayout);
            root.Controls.Add(grpActions, 1, 0);
            root.SetRowSpan(grpActions, 2);


            // Group 3: Inspection
            var grpInspect = CreateGroupBox("3. Inspection");
            var inspectLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(8) };
            inspectLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            inspectLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inspectLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            inspectLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            inspectLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            statusLabel = CreateLabel("Status: idle");
            statusLabel.ForeColor = System.Drawing.Color.Yellow;
            statusLabel.Font = new System.Drawing.Font(statusLabel.Font.FontFamily, 10, System.Drawing.FontStyle.Bold);
            statusLabel.Padding = new Padding(0, 4, 0, 4);
            inspectLayout.Controls.Add(statusLabel, 0, 0);
            inspectLayout.SetColumnSpan(statusLabel, 2);

            var snapshotLabel = CreateLabel("Snapshot:");
            snapshotLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            inspectLayout.Controls.Add(snapshotLabel, 0, 1);
            snapshotBox = CreateTextBox();
            toolTip.SetToolTip(snapshotBox, "Details of the captured snapshot state.");
            inspectLayout.Controls.Add(snapshotBox, 1, 1);

            var predictedLabel = CreateLabel("Predicted:");
            predictedLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            inspectLayout.Controls.Add(predictedLabel, 0, 2);
            predictedBox = CreateTextBox();
            toolTip.SetToolTip(predictedBox, "The bytes generated by the AI.");

            grpInspect.Controls.Add(inspectLayout);
            root.Controls.Add(grpInspect, 0, 2);
            root.SetColumnSpan(grpInspect, 2);

            Controls.Add(root);
        }

        private GroupBox CreateGroupBox(string text)
        {
            return new GroupBox 
            { 
                Text = text, 
                Dock = DockStyle.Fill, 
                ForeColor = System.Drawing.Color.Cyan,
                Padding = new Padding(10) 
            };
        }

        private Label CreateLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = System.Drawing.Color.White, Anchor = AnchorStyles.Left };
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

        private NumericUpDown CreateNumeric(decimal min, decimal max, decimal val, int decimals = 0, decimal inc = 1)
        {
             return new NumericUpDown
             {
                 Minimum = min,
                 Maximum = max,
                 Value = val,
                 DecimalPlaces = decimals,
                 Increment = inc,
                 Dock = DockStyle.Fill,
                 BackColor = System.Drawing.Color.FromArgb(60, 60, 60),
                 ForeColor = System.Drawing.Color.White
             };
        }

        private TextBox CreateTextBox()
        {
            return new TextBox 
            { 
                Multiline = true, 
                ReadOnly = true, 
                ScrollBars = ScrollBars.Vertical, 
                Dock = DockStyle.Fill,
                MinimumSize = new System.Drawing.Size(0, 60),
                BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
                ForeColor = System.Drawing.Color.LightGray,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new System.Drawing.Font("Consolas", 9)
            };
        }

        private void SetParams()
        {
            if (main.ImagineEngineInstance == null) return;
            main.ImagineEngineInstance.BytesToGenerate = (int)bytesUpDown.Value;
            main.ImagineEngineInstance.Temperature = (float)tempUpDown.Value / 40f;
            main.ImagineEngineInstance.TopK = topKUpDown.Value == 0 ? (int?)null : (int)topKUpDown.Value;
        }

        private void LoadModel()
        {
            if (main.ImagineEngineInstance == null)
            {
                statusLabel.Text = "Status: emulator not ready";
                return;
            }
            bool ok = main.ImagineEngineInstance.LoadModel((int)epochUpDown.Value);
            statusLabel.Text = ok ? $"Loaded epoch {(int)epochUpDown.Value}" : "Load failed";
        }

        private void CaptureSnapshot()
        {
            if (main.ImagineEngineInstance == null)
            {
                statusLabel.Text = "Status: emulator not ready";
                return;
            }
            try
            {
                var snap = main.ImagineEngineInstance.CaptureSnapshot();
                snapshotBox.Text = $"PC {snap.PC:X4}\r\nA {snap.A:X2} X {snap.X:X2} Y {snap.Y:X2} P {snap.P:X2} SP {snap.SP:X4}\r\nPRG {(snap.InPrgRom ? "YES" : "NO")}\r\nPrev8: {string.Join(" ", snap.Prev8.Select(b=>b.ToString("X2")))}\r\nNext16: {string.Join(" ", snap.Next16.Select(b=>b.ToString("X2")))}";
                statusLabel.Text = "Status: snapshot captured";
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error: {ex.Message}";
            }
        }

        private void Predict()
        {
            if (main.ImagineEngineInstance == null)
            {
                statusLabel.Text = "Status: emulator not ready";
                return;
            }
            try
            {
                var bytes = main.ImagineEngineInstance.PredictFromSnapshot();
                predictedBox.Text = string.Join(" ", bytes.Select(b => b.ToString("X2")));
                statusLabel.Text = $"Status: predicted {bytes.Length} bytes";
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error: {ex.Message}";
            }
        }

        private void ApplyPatch()
        {
            if (main.ImagineEngineInstance == null)
            {
                statusLabel.Text = "Status: emulator not ready";
                return;
            }
            try
            {
                if (main.ImagineEngineInstance.Snapshot == null || main.ImagineEngineInstance.PredictedBytes == null)
                {
                    statusLabel.Text = "Status: need snapshot + predict first";
                    return;
                }
                bool ok = main.ImagineEngineInstance.ApplyPatch(main.ImagineEngineInstance.Snapshot.PC, main.ImagineEngineInstance.PredictedBytes);
                statusLabel.Text = ok ? "Patch applied" : $"Patch failed: {main.ImagineEngineInstance.LastError}";
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error: {ex.Message}";
            }
        }

        private void ImagineBug()
        {
            if (main.ImagineEngineInstance == null)
            {
                statusLabel.Text = "Status: emulator not ready";
                return;
            }
            try
            {
                bool ok = main.ImagineEngineInstance.ImagineBug();
                statusLabel.Text = ok ? "Bug attempted" : $"Bug failed: {main.ImagineEngineInstance.LastError}";
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error: {ex.Message}";
            }
        }
    }
}
