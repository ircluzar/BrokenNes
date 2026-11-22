using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using NesEmulator.X86;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// Emulator > Install a Windows game... (nano98 install flow): the user's own game disc -> a .n98 that loads instantly.
    ///   1 Disc: a disc image file, or the PC's CD/DVD drive (read to an image first).  2 Machine: the seat preset + sound card.
    ///   3 Install: the seat runs in the main window; the user runs the disc's setup (install to D:\GAME), swaps discs when asked, clears any
    ///     Add New Hardware wizard.  4 Finish: Windows shuts down, the user picks the game's program and names it; the package is made and opened.
    /// Settle mode (step 2): no game; the hardware wizard of a new machine preset is cleared once and kept as a system layer.
    /// The same actions are on the HTTP API (/api/n98/install/ACTION) and the MCP server, for agents.
    /// </summary>
    public sealed class N98InstallWizard : Form
    {
        private readonly MainForm _app;
        private readonly Panel[] _pages = new Panel[4];
        private int _page;
        private readonly Label _title = new() { Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Padding = new Padding(8, 6, 0, 0) };
        private readonly Label _statusBar = new() { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 3, 0, 0), ForeColor = Color.DimGray };
        private readonly Button _back = new() { Text = "< Back", Width = 90 }, _next = new() { Text = "Next >", Width = 110 };
        private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };

        // page 1
        private readonly RadioButton _srcFile = new() { Text = "A disc image file (.iso, .cue, .bin)", AutoSize = true, Checked = true }, _srcDrive = new() { Text = "The disc in my CD/DVD drive", AutoSize = true };
        private readonly TextBox _discPath = new() { Width = 360 };
        private readonly ComboBox _drives = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ProgressBar _ripBar = new() { Width = 360, Visible = false };
        private readonly Label _winFiles = new() { AutoSize = true, MaximumSize = new Size(470, 0) };
        // page 2
        private readonly ComboBox _machine = new() { Width = 420, DropDownStyle = ComboBoxStyle.DropDownList }, _sound = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _settle = new() { Text = "Settle the hardware only (no game): clear Windows' Add New Hardware wizard once, keep it as a driver layer", AutoSize = true, MaximumSize = new Size(470, 0) };
        // page 3
        private readonly ListBox _setups = new() { Width = 300, Height = 80 };
        private readonly TextBox _runOther = new() { Width = 300 };
        private readonly Label _phase = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        // page 4
        private readonly ListBox _programs = new() { Width = 420, Height = 110 };
        private readonly TextBox _gameTitle = new() { Width = 300 }, _gameArgs = new() { Width = 300 }, _outPath = new() { Width = 360 }, _layerId = new() { Width = 200 };
        private readonly CheckBox _keepHw = new() { Text = "Keep hardware drivers it installed as a driver layer", AutoSize = true };
        private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(470, 0) };

        public N98InstallWizard(MainForm app)
        {
            _app = app;
            Text = "Install a Windows game"; ClientSize = new Size(500, 470); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; Location = new Point(Math.Max(0, app.Right - 20), app.Top + 40);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            buttons.Controls.Add(_next); buttons.Controls.Add(_back);
            _back.Click += (_, _) => Show(_page - 1);
            _next.Click += async (_, _) => await NextAsync();
            for (int i = 0; i < 4; i++) { _pages[i] = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), Visible = false, AutoScroll = true }; Controls.Add(_pages[i]); }
            Controls.Add(buttons); Controls.Add(_statusBar); Controls.Add(_title);
            BuildDisc(); BuildMachine(); BuildInstall(); BuildFinish();
            _poll.Tick += (_, _) => Poll();
            FormClosing += (_, e) =>
            {
                if (_page == 2 && _app.N98InstallSession is { PoweredOff: false } && MessageBox.Show(this, "Stop the install? Nothing is kept.", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) { e.Cancel = true; return; }
                _poll.Stop();
                if (_page >= 2) _app.CloseN98Install();
            };
            Show(0);
        }

        private static FlowLayoutPanel Flow(Panel p) { var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true }; p.Controls.Add(f); return f; }
        private static Label Text1(string t, bool bold = false) => new() { Text = t, AutoSize = true, MaximumSize = new Size(470, 0), Margin = new Padding(0, 6, 0, 2), Font = bold ? new Font("Segoe UI", 9f, FontStyle.Bold) : null };
        private static FlowLayoutPanel Row(params Control[] cs) { var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) }; r.Controls.AddRange(cs); return r; }
        private static Button Btn(string t, Action a) { var b = new Button { Text = t, AutoSize = true }; b.Click += (_, _) => a(); return b; }

        private void BuildDisc()
        {
            var f = Flow(_pages[0]);
            f.Controls.Add(Text1("Put your game's disc in, or pick an image of it. Your games stay on this PC: nothing is uploaded or shared."));
            f.Controls.Add(_srcFile);
            f.Controls.Add(Row(_discPath, Btn("Browse...", () =>
            {
                using var d = new OpenFileDialog { Filter = "Disc images (*.iso;*.cue;*.bin)|*.iso;*.cue;*.bin|All files|*.*" };
                if (d.ShowDialog(this) == DialogResult.OK) { _discPath.Text = d.FileName; _srcFile.Checked = true; }
            })));
            f.Controls.Add(_srcDrive);
            f.Controls.Add(Row(_drives, Btn("Refresh", FillDrives)));
            f.Controls.Add(_ripBar);
            if (!N98Seat.HasBase())
            {
                // no seat base yet: built once from the user's own Windows 98 SE disc + product key (the key is used for this build only, never saved)
                var iso = new TextBox { Width = 300 }; var key = new TextBox { Width = 260, CharacterCasing = CharacterCasing.Upper, PlaceholderText = "XXXXX-XXXXX-XXXXX-XXXXX-XXXXX" };
                f.Controls.Add(Text1("Windows 98 is not set up yet", true));
                f.Controls.Add(Text1("It is built once from your own Windows 98 Second Edition disc image and its product key (the key is only used for this build: it is not saved anywhere). It takes a few minutes."));
                f.Controls.Add(Row(iso, Btn("Browse...", () => { using var d = new OpenFileDialog { Filter = "Windows 98 SE disc image (*.iso)|*.iso|All files|*.*" }; if (d.ShowDialog(this) == DialogResult.OK) iso.Text = d.FileName; })));
                f.Controls.Add(Row(new Label { Text = "Product key", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, key));
                Button? build = null;
                build = Btn("Build Windows 98", () =>
                {
                    if (!N98BaseBuild.KeyLooksRight(key.Text)) { Busy("the product key is 5 groups of 5 letters/digits"); return; }
                    if (!File.Exists(iso.Text.Trim())) { Busy("pick the Windows 98 SE disc image"); return; }
                    build!.Enabled = false; string k = key.Text; key.Text = "";
                    Task.Run(() => N98BaseBuild.Run(iso.Text.Trim(), k, log: l => BeginInvoke(() => Busy(l.Length > 90 ? l[..90] : l)))).ContinueWith(t => BeginInvoke(() =>
                    {
                        build.Enabled = true;
                        Busy(t.IsFaulted ? t.Exception!.InnerException!.Message : t.Result["ok"]?.GetValue<bool>() == true ? "Windows 98 is ready: " + t.Result["base"] : t.Result["error"]?.GetValue<string>() ?? "?");
                    }));
                });
                f.Controls.Add(build);
            }
            f.Controls.Add(Text1("Windows 98 files", true));
            f.Controls.Add(_winFiles);
            f.Controls.Add(Btn("Choose my Windows 98 SE disc image...", () =>
            {
                using var d = new OpenFileDialog { Filter = "Windows 98 SE disc image (*.iso)|*.iso|All files|*.*" };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Busy("reading the Windows 98 SE disc...");
                Task.Run(() => N98Install.MakeWindowsFilesDisc(d.FileName)).ContinueWith(t => BeginInvoke(() =>
                {
                    Busy(t.IsFaulted ? "could not read it: " + t.Exception!.InnerException!.Message : t.Result["ok"]?.GetValue<bool>() == true ? "Windows 98 files ready" : t.Result["error"]?.GetValue<string>() ?? "?");
                    FillWinFiles();
                }));
            }));
            FillDrives(); FillWinFiles();
        }

        private void FillDrives()
        {
            _drives.Items.Clear();
            foreach (var (drive, label) in N98Install.HostDiscDrives()) _drives.Items.Add($"{drive} {label}");
            if (_drives.Items.Count > 0) _drives.SelectedIndex = 0; else _drives.Items.Add("(no disc in a drive)");
        }

        private void FillWinFiles() => _winFiles.Text = N98Install.HasWindowsFilesDisc
            ? "Ready: when a game's setup makes Windows ask for its own CD, the Install step has a button that puts it in."
            : "Some setups install Windows parts and then Windows asks for its own CD. Give your Windows 98 SE disc image once (optional; only the files Windows asks for are kept, on this PC).";

        private void BuildMachine()
        {
            var f = Flow(_pages[1]);
            f.Controls.Add(Text1("The PC the game runs on. The standard nano98 seat fits almost every game."));
            foreach (var (key, label) in N98Install.Machines()) _machine.Items.Add($"{key}  -  {label}");
            _machine.SelectedIndex = 0;
            f.Controls.Add(Text1("Machine")); f.Controls.Add(_machine);
            foreach (var c in PcSound.CardOptions().Where(c => c.family == "Sound Blaster")) _sound.Items.Add(c.id);
            _sound.SelectedItem = "SB16_D072"; if (_sound.SelectedIndex < 0 && _sound.Items.Count > 0) _sound.SelectedIndex = 0;
            f.Controls.Add(Text1("Sound card")); f.Controls.Add(_sound);
            f.Controls.Add(_settle);
        }

        private void BuildInstall()
        {
            var f = Flow(_pages[2]);
            f.Controls.Add(_phase);
            foreach (var n in N98Install.Note) f.Controls.Add(Text1("- " + n));
            f.Controls.Add(Text1("The game's setup (on the disc)", true));
            f.Controls.Add(Row(_setups, Btn("Run", () => Act("run", new JsonObject { ["path"] = _setups.SelectedItem?.ToString() ?? "" }))));
            f.Controls.Add(Row(_runOther, Btn("Run this", () => Act("run", new JsonObject { ["path"] = _runOther.Text.Trim() }))));
            f.Controls.Add(Text1("Discs", true));
            f.Controls.Add(Row(
                Btn("Change disc...", () =>
                {
                    using var d = new OpenFileDialog { Filter = "Disc images (*.iso;*.cue;*.bin)|*.iso;*.cue;*.bin|All files|*.*" };
                    if (d.ShowDialog(this) == DialogResult.OK) Act("disc", new JsonObject { ["path"] = d.FileName });
                }),
                Btn("Windows asks for its CD", () => Act("winfiles", new JsonObject())),
                Btn("Game disc back", () => Act("disc", new JsonObject { ["path"] = _gameDisc }))));
            f.Controls.Add(Text1("When Windows asks for a disc: put the one it names in with these buttons, then click OK (or press Enter) in Windows."));
            f.Controls.Add(Text1("Windows", true));
            f.Controls.Add(Row(Btn("Restart Windows", () => Act("restart", new JsonObject())), Btn("Open Explorer", () => Act("explorer", new JsonObject()))));
            f.Controls.Add(Text1("When the setup has finished, click \"Setup is done\": Windows shuts down and the game is packed."));
        }

        private void BuildFinish()
        {
            var f = Flow(_pages[3]);
            f.Controls.Add(Text1("The game's program (the one that starts the game)", true));
            f.Controls.Add(_programs);
            f.Controls.Add(Text1("Name")); f.Controls.Add(_gameTitle);
            f.Controls.Add(Text1("Start options (optional)")); f.Controls.Add(_gameArgs);
            f.Controls.Add(Text1("Save the game as")); f.Controls.Add(Row(_outPath, Btn("...", () =>
            {
                using var d = new SaveFileDialog { Filter = "nano98 game (*.n98)|*.n98", FileName = Path.GetFileName(_outPath.Text) };
                if (d.ShowDialog(this) == DialogResult.OK) _outPath.Text = d.FileName;
            })));
            f.Controls.Add(Row(_keepHw, _layerId));
            f.Controls.Add(_result);
            _gameTitle.TextChanged += (_, _) => { if (_outPath.Tag == null) _outPath.Text = Path.Combine(N98Install.DefaultPackageFolder, N98Install.SafeName(_gameTitle.Text) + ".n98"); };
            _outPath.KeyPress += (_, _) => _outPath.Tag = "user";
            _programs.SelectedIndexChanged += (_, _) =>
            {
                if (_gameTitle.Tag == null && _programs.SelectedItem is string p) { _gameTitle.Text = Path.GetFileNameWithoutExtension(p.Split(' ')[0]); }
            };
            _gameTitle.KeyPress += (_, _) => _gameTitle.Tag = "user";
        }

        private string? _gameDisc;

        private void Show(int i)
        {
            if (i < 0) return;
            _page = i;
            for (int k = 0; k < 4; k++) _pages[k].Visible = k == i;
            _title.Text = new[] { "1. Your game's disc", "2. The PC", "3. Install", "4. Finish" }[i];
            _back.Enabled = i == 1;
            _next.Text = i switch { 0 => "Next >", 1 => "Start the PC", 2 => "Setup is done >", _ => _settle.Checked ? "Make the driver layer" : "Make the game" };
            _next.Enabled = true;
        }

        private void Busy(string s) => _statusBar.Text = s;

        private async Task NextAsync()
        {
            try
            {
                _next.Enabled = false;
                switch (_page)
                {
                    case 0:
                        if (_srcFile.Checked)
                        {
                            if (!File.Exists(_discPath.Text.Trim())) { Busy("pick the disc image first"); return; }
                            _gameDisc = _discPath.Text.Trim();
                        }
                        else
                        {
                            string drv = (_drives.SelectedItem?.ToString() ?? "").Split(' ')[0];
                            if (drv.Length != 2) { Busy("no disc in a drive"); return; }
                            string iso = Path.Combine(N98Seat.DiscFolder, N98Install.SafeName((_drives.SelectedItem!.ToString()!.Length > 3 ? _drives.SelectedItem.ToString()![3..] : "disc").Trim()) + ".iso");
                            Directory.CreateDirectory(N98Seat.DiscFolder);
                            _ripBar.Visible = true; Busy("reading the disc...");
                            _gameDisc = await Task.Run(() => N98Install.RipDisc(drv, iso, (d, t) => BeginInvoke(() => { _ripBar.Maximum = (int)Math.Max(1, t); _ripBar.Value = (int)Math.Min(d, t); })));
                            _ripBar.Visible = false; Busy("disc read: " + _gameDisc);
                        }
                        Show(1);
                        break;
                    case 1:
                        {
                            var o = new N98Install.InstallOptions { Disc = _settle.Checked ? null : _gameDisc, Machine = _machine.SelectedItem!.ToString()!.Split(' ')[0], Sound = _sound.SelectedItem?.ToString() ?? "SB16_D072", Settle = _settle.Checked };
                            Busy("starting Windows...");
                            var ins = await _app.StartN98Install(o);
                            _setups.Items.Clear();
                            if (_gameDisc != null && !_settle.Checked) foreach (var s in N98Install.SetupCandidates(_gameDisc)) _setups.Items.Add(s);
                            if (_setups.Items.Count > 0) _setups.SelectedIndex = 0;
                            Show(2); _poll.Start();
                            break;
                        }
                    case 2:
                        {
                            var ins = _app.N98InstallSession;
                            if (ins == null) { Busy("the install PC is not running"); return; }
                            if (!ins.PoweredOff) { Busy("Windows is shutting down..."); await _app.N98InstallDo("poweroff", new JsonObject()); for (int i = 0; i < 600 && !ins.PoweredOff; i++) await Task.Delay(100); }
                            if (!ins.PoweredOff) { Busy("Windows did not shut down: close the open programs in it, then try again"); return; }
                            await ToFinish();
                            break;
                        }
                    case 3: await MakeAsync(); break;
                }
            }
            catch (Exception e) { Busy(e.Message); MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { if (!IsDisposed) _next.Enabled = true; }
        }

        private async Task ToFinish()
        {
            _poll.Stop();
            Show(3);
            _layerId.Text = "hw-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
            if (_settle.Checked) { _programs.Enabled = _gameTitle.Enabled = _gameArgs.Enabled = _outPath.Enabled = false; _keepHw.Checked = true; _keepHw.Enabled = false; return; }
            Busy("looking for the game's programs...");
            var res = await _app.N98InstallDo("programs", new JsonObject());
            _programs.Items.Clear();
            foreach (var p in res["programs"]!.AsArray()) _programs.Items.Add($"{p!["path"]}   ({p["size"]!.GetValue<long>() / 1024:N0} KB)");
            if (_programs.Items.Count > 0) _programs.SelectedIndex = 0;
            Busy(_programs.Items.Count == 0 ? "no program on D:\\GAME: the game was not installed there (go back is not possible; start again and install to D:\\GAME)" : "pick the game's program and name it");
        }

        private async Task MakeAsync()
        {
            if (_settle.Checked)
            {
                Busy("making the driver layer...");
                var r = await _app.N98InstallDo("capture-layer", new JsonObject { ["id"] = _layerId.Text.Trim() });
                _result.Text = "Driver layer: " + r["layer"] + Environment.NewLine + "Packages made on this machine can name it (system.layers).";
                Busy("done");
                return;
            }
            if (_programs.SelectedItem is not string sel || _gameTitle.Text.Trim().Length == 0) { Busy("pick the program and give the game a name"); return; }
            Busy("making the game (a minute for a big game)...");
            var a = new JsonObject { ["title"] = _gameTitle.Text.Trim(), ["exe"] = sel.Split("   ")[0].Trim(), ["out"] = _outPath.Text.Trim(), ["args"] = _gameArgs.Text.Trim() };
            if (_keepHw.Checked && _layerId.Text.Trim().Length > 0) a["layer_id"] = _layerId.Text.Trim();
            var res = await _app.N98InstallDo("capture", a);
            _result.Text = $"Made {res["package"]}: {res["d_files"]} files on the game drive, {res["c_files"]} Windows files, {res["registry_ops"]} settings"
                + (res["layer"] is JsonNode l && l.GetValue<string?>() != null ? $", driver layer {l}" : "") + ". Opening it...";
            string pkg = res["package"]!.GetValue<string>();
            _app.CloseN98Install();
            _app.OpenN98Package(pkg);
            Busy("done: the game opens; from now on it loads in seconds (Emulator > Windows 98 games)");
            _next.Visible = false;
        }

        private async void Act(string action, JsonObject a)
        {
            try { await _app.N98InstallDo(action, a); Busy(action + ": ok"); }
            catch (Exception e) { Busy(e.Message); }
        }

        private void Poll()
        {
            var ins = _app.N98InstallSession;
            if (ins == null) { _phase.Text = "The install PC is not running."; return; }
            _phase.Text = ins.Phase switch
            {
                "booting" => "Windows is starting...", "ready" => "Ready: run the setup.", "running" => "Setup is running in the main window.",
                "powering off" => "Windows is shutting down...", "restarting" => "Windows is restarting...", "off" => "Windows is off.", _ => ins.Phase,
            } + (ins.Disc != null ? "   Disc: " + Path.GetFileName(ins.Disc) : "");
        }

        /// <summary>The seat powered off (the user shut Windows down themselves, or "Setup is done").</summary>
        internal void SeatEnded(string kind, string detail)
        {
            if (kind == "poweroff" && _page == 2) _ = BeginInvoke(async () => { try { await ToFinish(); } catch (Exception e) { Busy(e.Message); } });
            else if (kind is "crash" or "fail") Busy($"Windows stopped ({detail}). Restart Windows, or close and start again.");
        }
    }
}
