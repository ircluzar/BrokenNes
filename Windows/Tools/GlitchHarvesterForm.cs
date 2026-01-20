using System;
using System.Linq;
using System.Windows.Forms;
using BrokenNes.CorruptorModels;
using NesEmulator;

namespace BrokenNes.Windows.Tools
{
    internal sealed class GlitchHarvesterForm : Form
    {
        private readonly MainForm main;
        private TextBox baseNameBox;
        private ListBox baseList;
        private ListBox stashList;
        private ListBox stockList;
        private CheckBox loadOnOperationCheckbox;

        private ToolTip toolTip;

        public GlitchHarvesterForm(MainForm main)
        {
            this.main = main;
            Text = "Glitch Harvester";
            Width = 950;
            Height = 700;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            // Dark Theme
            BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            ForeColor = System.Drawing.Color.White;

            InitializeUi();
            main.CorruptorStateChanged += OnStateChanged;
            FormClosed += (_, __) => main.CorruptorStateChanged -= OnStateChanged;
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
                RowCount = 2, 
                Padding = new Padding(12),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));

            // Section 1: Base States
            var grpBase = CreateGroupBox("1. SaveStates");
            var baseLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(5) };
            baseLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            baseLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            baseLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var newBasePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Padding = new Padding(0, 2, 0, 2) };
            newBasePanel.Controls.Add(CreateLabel("New Name:"));
            baseNameBox = new TextBox { Width = 180, Height = 24, BackColor = System.Drawing.Color.FromArgb(60, 60, 60), ForeColor = System.Drawing.Color.White, BorderStyle = BorderStyle.FixedSingle };
            toolTip.SetToolTip(baseNameBox, "Enter a name for the new base state.");
            newBasePanel.Controls.Add(baseNameBox);
            
            var addBtn = CreateButton("Add Base");
            addBtn.Click += (_, __) => AddBase();
            toolTip.SetToolTip(addBtn, "Save current emulator state as a Base.");
            newBasePanel.Controls.Add(addBtn);
            baseLayout.Controls.Add(newBasePanel, 0, 0);

            baseList = CreateListBox();
            baseList.SelectedIndexChanged += (_, __) => SelectBase();
            toolTip.SetToolTip(baseList, "Select a base state.");
            baseLayout.Controls.Add(baseList, 0, 1);

            var baseActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            var loadBtn = CreateButton("Load"); loadBtn.Click += (_, __) => LoadBase();
            toolTip.SetToolTip(loadBtn, "Load selected base state.");
            var delBtn = CreateButton("Delete"); delBtn.Click += (_, __) => DeleteBase();
            toolTip.SetToolTip(delBtn, "Delete selected base state.");
            baseActions.Controls.Add(loadBtn); baseActions.Controls.Add(delBtn);
            baseLayout.Controls.Add(baseActions, 0, 2);
            
            grpBase.Controls.Add(baseLayout);
            root.Controls.Add(grpBase, 0, 0);

            // Section 2: Stash
            var grpStash = CreateGroupBox("2. Stash History");
            var stashLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(5) };
            stashLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stashLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stashLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Load on operation checkbox
            loadOnOperationCheckbox = new CheckBox 
            { 
                Text = "Load on operation", 
                Checked = true,
                AutoSize = true, 
                ForeColor = System.Drawing.Color.White,
                Padding = new Padding(0, 2, 0, 2)
            };
            loadOnOperationCheckbox.CheckedChanged += (_, __) => 
            {
                main.RunOnEmulationThreadAsync(() =>
                {
                    main.Corruptor.GhLoadOnOperation = loadOnOperationCheckbox.Checked;
                });
            };
            toolTip.SetToolTip(loadOnOperationCheckbox, "When checked, loads the selected savestate before blasting and replaying to ensure consistent results.");
            stashLayout.Controls.Add(loadOnOperationCheckbox, 0, 0);

            stashList = CreateListBox();
            toolTip.SetToolTip(stashList, "Corruptions are auto-added here on blast.");
            stashLayout.Controls.Add(stashList, 0, 1);

            var stashActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            var blastToStashBtn = CreateButton("Blast"); blastToStashBtn.Click += (_, __) => BlastToStash();
            toolTip.SetToolTip(blastToStashBtn, "Blast now and send result to Stash History.");
            var replayStashBtn = CreateButton("Replay"); replayStashBtn.Click += (_, __) => ReplaySelected(stashList, false);
            toolTip.SetToolTip(replayStashBtn, "Replay this corruption.");
            var keepBtn = CreateButton("Keep"); keepBtn.Click += (_, __) => PromoteSelected();
            toolTip.SetToolTip(keepBtn, "Save to Stockpile.");
            var clearBtn = CreateButton("Clear"); clearBtn.Click += (_, __) => ClearStash();
            toolTip.SetToolTip(clearBtn, "Clear the stash.");
            stashActions.Controls.Add(blastToStashBtn); stashActions.Controls.Add(replayStashBtn); stashActions.Controls.Add(keepBtn); stashActions.Controls.Add(clearBtn);
            stashLayout.Controls.Add(stashActions, 0, 2);
            
            grpStash.Controls.Add(stashLayout);
            root.Controls.Add(grpStash, 1, 0);

            // Section 3: Stockpile
            var grpStock = CreateGroupBox("3. Stockpile Manager");
            var stockLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(5) };
            stockLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stockLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            stockList = CreateListBox();
            toolTip.SetToolTip(stockList, "Saved glitches survive app restart.");
            stockLayout.Controls.Add(stockList, 0, 0);

            var stockActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            var replayStockBtn = CreateButton("Replay"); replayStockBtn.Click += (_, __) => ReplaySelected(stockList, true);
            toolTip.SetToolTip(replayStockBtn, "Replay saved glitch.");
            var deleteStockBtn = CreateButton("Delete"); deleteStockBtn.Click += (_, __) => DeleteStock();
            toolTip.SetToolTip(deleteStockBtn, "Delete from stockpile.");
            stockActions.Controls.Add(replayStockBtn); stockActions.Controls.Add(deleteStockBtn);
            stockLayout.Controls.Add(stockActions, 0, 1);

            grpStock.Controls.Add(stockLayout);
            root.Controls.Add(grpStock, 0, 1);
            root.SetColumnSpan(grpStock, 2);

            Controls.Add(root);
        }

        private GroupBox CreateGroupBox(string text)
        {
            return new GroupBox 
            { 
                Text = text, 
                Dock = DockStyle.Fill, 
                ForeColor = System.Drawing.Color.Cyan, 
                Padding = new Padding(12),
                Margin = new Padding(4)
            };
        }

        private Label CreateLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = System.Drawing.Color.White, Padding = new Padding(0, 6, 0, 0) };
        }

        private Button CreateButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new System.Drawing.Size(75, 28),
                Margin = new Padding(4, 2, 4, 2),
                FlatStyle = FlatStyle.Flat,
                BackColor = System.Drawing.Color.FromArgb(60, 60, 60),
                ForeColor = System.Drawing.Color.White
            };
            btn.FlatAppearance.BorderColor = System.Drawing.Color.Gray;
            return btn;
        }

        private ListBox CreateListBox()
        {
            return new ListBox 
            { 
                Dock = DockStyle.Fill, 
                BackColor = System.Drawing.Color.FromArgb(30, 30, 30), 
                ForeColor = System.Drawing.Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                ItemHeight = 20,
                Font = new System.Drawing.Font("Segoe UI", 9)
            };
        }

        private void AddBase()
        {
            if (!main.IsEmulatorReady) return;
            var name = string.IsNullOrWhiteSpace(baseNameBox.Text) ? $"Base {main.Corruptor.GhBaseStates.Count + 1}" : baseNameBox.Text.Trim();
            main.WithNes(n =>
            {
                main.Corruptor.GhNewBaseName = name;
                main.Corruptor.GhAddBaseState(n);
                main.RefreshMemoryDomainsRequested();
                // Don't call RaiseCorruptorChangedPublic here - RefreshMemoryDomainsRequested already triggers it
            });
        }

        private void SelectBase()
        {
            if (baseList.SelectedItem is EntryItem item)
            {
                main.Corruptor.GhSelectedBaseId = item.Id;
            }
        }

        private void LoadBase()
        {
            if (baseList.SelectedItem is EntryItem item)
            {
                // Get snapshot first to safely access corruptor data
                var snapshot = main.GetCorruptorSnapshot();
                if (snapshot == null) return;
                
                var b = snapshot.GhBaseStates.FirstOrDefault(x => x.Id == item.Id);
                if (b == null) return;
                
                // Capture the state string before entering emulation thread
                string stateToLoad = b.State;
                string baseId = b.Id;
                
                main.WithNes(n =>
                {
                    n.LoadState(stateToLoad);
                    main.Corruptor.GhSelectedBaseId = baseId;
                    main.RefreshMemoryDomainsRequested(); // Rebuild memory domains like QuickLoad does
                });
            }
        }

        private void DeleteBase()
        {
            if (baseList.SelectedItem is EntryItem item)
            {
                main.RunOnEmulationThreadAsync(() =>
                {
                    main.Corruptor.GhSelectedBaseId = item.Id;
                    main.Corruptor.GhDeleteSelectedBase();
                    main.RaiseCorruptorChangedPublic();
                });
            }
        }

        private void ReplaySelected(ListBox list, bool fromStockpile)
        {
            if (list.SelectedItem is EntryItem item)
            {
                main.WithNes(n =>
                {
                    var entry = (fromStockpile ? main.Corruptor.GhStockpile : main.Corruptor.GhStash).FirstOrDefault(e => e.Id == item.Id);
                    if (entry == null) return;
                    
                    // Use bundled state if available, otherwise fall back to base state lookup
                    string? stateToLoad = null;
                    if (!string.IsNullOrEmpty(entry.State))
                    {
                        stateToLoad = entry.State;
                    }
                    else
                    {
                        var baseState = main.Corruptor.GhBaseStates.FirstOrDefault(b => b.Id == entry.BaseStateId);
                        if (baseState != null) stateToLoad = baseState.State;
                    }
                    
                    if (stateToLoad == null) return;
                    
                    n.LoadState(stateToLoad);
                    main.Corruptor.ApplyBlastLayer(entry.Writes, n);
                });
            }
        }

        private void PromoteSelected()
        {
            if (stashList.SelectedItem is EntryItem item)
            {
                main.RunOnEmulationThreadAsync(() =>
                {
                    var entry = main.Corruptor.GhStash.FirstOrDefault(e => e.Id == item.Id);
                    if (entry == null) return;
                    main.Corruptor.GhPromoteEntry(entry);
                    main.RaiseCorruptorChangedPublic();
                });
            }
        }

        private void ClearStash()
        {
            main.RunOnEmulationThreadAsync(() =>
            {
                main.Corruptor.GhClearStash();
                main.RaiseCorruptorChangedPublic();
            });
        }

        private void BlastToStash()
        {
            main.WithNes(n =>
            {
                main.Corruptor.GhStashFromBlast(n);
                main.RaiseCorruptorChangedPublic();
            });
        }

        private void DeleteStock()
        {
            if (stockList.SelectedItem is EntryItem item)
            {
                main.RunOnEmulationThreadAsync(() =>
                {
                    main.Corruptor.GhDeleteStock(item.Id);
                    main.RaiseCorruptorChangedPublic();
                });
            }
        }

        private void RefreshUi()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshUi)); return; }
            var snapshot = main.GetCorruptorSnapshot();
            if (snapshot == null) return; // Lock unavailable, skip this refresh

            // Preserve selected base ID before clearing
            var selectedBaseId = snapshot.GhSelectedBaseId;

            // Update checkbox state
            loadOnOperationCheckbox.Checked = snapshot.GhLoadOnOperation;

            baseList.Items.Clear();
            int selectedIndex = -1;
            int idx = 0;
            foreach (var b in snapshot.GhBaseStates)
            {
                baseList.Items.Add(new EntryItem(b.Id, b.Name, b.Created, 0));
                if (b.Id == selectedBaseId) selectedIndex = idx;
                idx++;
            }
            // Restore selection if we found it
            if (selectedIndex >= 0 && selectedIndex < baseList.Items.Count)
            {
                baseList.SelectedIndex = selectedIndex;
            }

            stashList.Items.Clear();
            foreach (var e in snapshot.GhStash.OrderByDescending(e => e.Created))
            {
                stashList.Items.Add(new EntryItem(e.Id, $"{e.Name} ({e.Writes.Count} w)", e.Created, e.Writes.Count));
            }

            stockList.Items.Clear();
            foreach (var e in snapshot.GhStockpile.OrderByDescending(e => e.Created))
            {
                stockList.Items.Add(new EntryItem(e.Id, $"{e.Name} ({e.Writes.Count} w)", e.Created, e.Writes.Count));
            }
        }

        private void OnStateChanged() => RefreshUi();

        private sealed record EntryItem(string Id, string Label, DateTime Created, int Writes)
        {
            public override string ToString() => $"{Label} • {Created.ToLocalTime():HH:mm:ss}";
        }
    }
}
