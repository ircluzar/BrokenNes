using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BrokenNes.Windows.Rendering;
using BrokenNes.Workshop.Tas.SelfPlay;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// The interactive debug host. Deliberately plain WinForms + GDI+ (DirectBitmap, linked from
/// Windows/Rendering/) - no SharpDX, no shaders, no WebView2/Webmodules, no achievements/cards/
/// corruptor. Everything here calls an already-existing NES.cs method; see Web/Emulation/
/// Emulator.cs for the equivalent "thin host" pattern used by the Blazor build.
/// </summary>
public sealed class WorkshopForm : Form
{
    private const int NesWidth = 256;
    private const int NesHeight = 240;
    private const int PixelScale = 2;

    private NES? _nes;
    private byte[]? _romBytes;
    private string _romName = string.Empty;
    private readonly DirectBitmap _directBitmap = new(NesWidth, NesHeight);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly bool[] _p1 = new bool[8];
    private readonly bool[] _p2 = new bool[8];
    private long _frameCount;
    private string? _savedState;

    // Self-play (SMB1) - see Tas/SelfPlay/SelfPlayManager.cs. The ROM lives in the sibling
    // ML_NesPlayer project (not shipped with BrokenNes); if not found at this path, the
    // "Self-Play (SMB1)" checkbox falls back to whatever ROM is already loaded instead of failing.
    private static readonly string Smb1RomPath = Path.Combine(
        @"C:\Users\philt\OneDrive\Documents\PROJECTS\!!! Experiments\ML_NesPlayer\TAS\Nintendo Entertainment System",
        "Super Mario Bros. (JU) (PRG0) [!].nes");
    private SelfPlayManager? _selfPlayManager;

    // UI
    private readonly PictureBox _screen = new();
    private readonly Button _playPauseBtn = new() { Text = "Play" };
    private readonly Button _resetBtn = new() { Text = "Reset" };
    private readonly Button _stepFrameBtn = new() { Text = "Step Frame" };
    private readonly Button _stepInstructionBtn = new() { Text = "Step Instr." };
    private readonly Button _openRomBtn = new() { Text = "Open ROM..." };
    private readonly CheckBox _strictAccuracyCheck = new() { Text = "Strict accuracy (disable speed hacks)", AutoSize = true };
    private readonly CheckBox _selfPlayCheck = new() { Text = "Self-Play (SMB1) - connects to Python inference server", AutoSize = true };
    private readonly Label _statusLabel = new() { AutoSize = true };
    private readonly ComboBox _cpuCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _ppuCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _apuCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _registersBox = new() { Multiline = true, ReadOnly = true, Font = new Font(FontFamily.GenericMonospace, 9f) };
    private readonly ComboBox _memDomainCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _memAddressBox = new() { Text = "0000" };
    private readonly TextBox _memDumpBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 9f) };
    private readonly Button _memRefreshBtn = new() { Text = "Refresh" };
    private readonly Button _saveStateBtn = new() { Text = "Save State" };
    private readonly Button _loadStateBtn = new() { Text = "Load State" };
    private readonly Button _benchmarkBtn = new() { Text = "Run Benchmarks" };

    public WorkshopForm()
    {
        Text = "BrokenNes Workshop";
        Width = 1180;
        Height = 800;
        KeyPreview = true;

        BuildLayout();
        WireEvents();
        PopulateCoreSelectors();

        _timer.Tick += Timer_Tick;

        LoadBootRom();
    }

    // ---- Layout ---------------------------------------------------------

    private void BuildLayout()
    {
        _screen.Width = NesWidth * PixelScale;
        _screen.Height = NesHeight * PixelScale;
        _screen.Location = new Point(12, 12);
        _screen.BorderStyle = BorderStyle.FixedSingle;
        _screen.SizeMode = PictureBoxSizeMode.StretchImage;
        Controls.Add(_screen);

        var transport = new FlowLayoutPanel
        {
            Location = new Point(12, _screen.Bottom + 8),
            Width = _screen.Width,
            Height = 32,
            FlowDirection = FlowDirection.LeftToRight,
        };
        transport.Controls.AddRange(new Control[] { _playPauseBtn, _resetBtn, _stepFrameBtn, _stepInstructionBtn, _openRomBtn });
        Controls.Add(transport);

        _statusLabel.Location = new Point(12, transport.Bottom + 6);
        Controls.Add(_statusLabel);

        _strictAccuracyCheck.Location = new Point(12, transport.Bottom + 26);
        Controls.Add(_strictAccuracyCheck);

        _selfPlayCheck.Location = new Point(12, transport.Bottom + 48);
        Controls.Add(_selfPlayCheck);

        int rightX = _screen.Right + 20;

        var coresGroup = new GroupBox { Text = "Cores", Location = new Point(rightX, 12), Width = 340, Height = 110 };
        coresGroup.Controls.Add(new Label { Text = "CPU", Location = new Point(10, 25), AutoSize = true });
        _cpuCombo.Location = new Point(60, 22); _cpuCombo.Width = 260;
        coresGroup.Controls.Add(_cpuCombo);
        coresGroup.Controls.Add(new Label { Text = "PPU", Location = new Point(10, 55), AutoSize = true });
        _ppuCombo.Location = new Point(60, 52); _ppuCombo.Width = 260;
        coresGroup.Controls.Add(_ppuCombo);
        coresGroup.Controls.Add(new Label { Text = "APU", Location = new Point(10, 85), AutoSize = true });
        _apuCombo.Location = new Point(60, 82); _apuCombo.Width = 260;
        coresGroup.Controls.Add(_apuCombo);
        Controls.Add(coresGroup);

        var regGroup = new GroupBox { Text = "CPU Registers", Location = new Point(rightX, coresGroup.Bottom + 10), Width = 340, Height = 90 };
        _registersBox.Location = new Point(10, 20); _registersBox.Width = 320; _registersBox.Height = 60;
        regGroup.Controls.Add(_registersBox);
        Controls.Add(regGroup);

        var stateGroup = new GroupBox { Text = "State", Location = new Point(rightX, regGroup.Bottom + 10), Width = 340, Height = 55 };
        _saveStateBtn.Location = new Point(10, 20); _saveStateBtn.Width = 100;
        _loadStateBtn.Location = new Point(118, 20); _loadStateBtn.Width = 100;
        _benchmarkBtn.Location = new Point(226, 20); _benchmarkBtn.Width = 104;
        stateGroup.Controls.AddRange(new Control[] { _saveStateBtn, _loadStateBtn, _benchmarkBtn });
        Controls.Add(stateGroup);

        var memGroup = new GroupBox { Text = "Memory Viewer", Location = new Point(rightX, stateGroup.Bottom + 10), Width = 340, Height = 400 };
        memGroup.Controls.Add(new Label { Text = "Domain", Location = new Point(10, 25), AutoSize = true });
        _memDomainCombo.Location = new Point(65, 22); _memDomainCombo.Width = 150;
        memGroup.Controls.Add(_memDomainCombo);
        memGroup.Controls.Add(new Label { Text = "Addr", Location = new Point(10, 55), AutoSize = true });
        _memAddressBox.Location = new Point(65, 52); _memAddressBox.Width = 60;
        memGroup.Controls.Add(_memAddressBox);
        _memRefreshBtn.Location = new Point(225, 51); _memRefreshBtn.Width = 90;
        memGroup.Controls.Add(_memRefreshBtn);
        _memDumpBox.Location = new Point(10, 82); _memDumpBox.Width = 318; _memDumpBox.Height = 300;
        memGroup.Controls.Add(_memDumpBox);
        Controls.Add(memGroup);
    }

    private void WireEvents()
    {
        _playPauseBtn.Click += (_, __) => TogglePlay();
        _resetBtn.Click += (_, __) => ResetRom();
        _stepFrameBtn.Click += (_, __) => StepFrame();
        _stepInstructionBtn.Click += (_, __) => StepInstruction();
        _openRomBtn.Click += (_, __) => OpenRomDialog();
        _strictAccuracyCheck.CheckedChanged += (_, __) => ApplyStrictAccuracyMode(_strictAccuracyCheck.Checked);
        _selfPlayCheck.CheckedChanged += (_, __) => ToggleSelfPlay(_selfPlayCheck.Checked);
        _cpuCombo.SelectedIndexChanged += (_, __) => { if (_cpuCombo.SelectedItem is string s) { _nes?.SetCpuCore(s); RefreshRegisters(); } };
        _ppuCombo.SelectedIndexChanged += (_, __) => { if (_ppuCombo.SelectedItem is string s) _nes?.SetPpuCore(s); };
        _apuCombo.SelectedIndexChanged += (_, __) => { if (_apuCombo.SelectedItem is string s) _nes?.SetApuCore(s); };
        _memDomainCombo.SelectedIndexChanged += (_, __) => RefreshMemoryView();
        _memRefreshBtn.Click += (_, __) => RefreshMemoryView();
        _saveStateBtn.Click += (_, __) => SaveState();
        _loadStateBtn.Click += (_, __) => LoadState();
        _benchmarkBtn.Click += (_, __) => RunBenchmarks();

        KeyDown += (_, e) => ApplyKey(e.KeyCode, true);
        KeyUp += (_, e) => ApplyKey(e.KeyCode, false);
    }

    private void PopulateCoreSelectors()
    {
        _cpuCombo.Items.AddRange(CoreRegistry.CpuIds.ToArray<object>());
        _ppuCombo.Items.AddRange(CoreRegistry.PpuIds.ToArray<object>());
        _apuCombo.Items.AddRange(CoreRegistry.ApuIds.ToArray<object>());
    }

    // ---- ROM loading ------------------------------------------------------

    private void LoadBootRom()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Roms", "test.nes");
            if (File.Exists(path))
                LoadRom(File.ReadAllBytes(path), "test.nes");
        }
        catch (Exception ex)
        {
            SetStatus($"Boot ROM load failed: {ex.Message}");
        }
    }

    private void OpenRomDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "NES ROMs (*.nes)|*.nes|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            LoadRom(File.ReadAllBytes(dlg.FileName), Path.GetFileName(dlg.FileName));
        }
        catch (Cartridge.UnsupportedMapperException ex)
        {
            MessageBox.Show(this, $"Unsupported mapper {ex.MapperId} ({ex.MapperName}).", "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadRom(byte[] rom, string name)
    {
        _timer.Stop();
        // A new ROM invalidates any in-progress self-play session (checkpoints reference the old
        // game's state) - drop it and let ToggleSelfPlay recreate one on demand.
        _selfPlayManager?.DisconnectPipe();
        _selfPlayManager = null;
        var nes = new NES { RomName = name };
        nes.LoadROM(rom);
        _nes = nes;
        _romBytes = rom;
        _romName = name;
        _frameCount = 0;
        SyncCoreSelectorsFromNes();
        PopulateMemoryDomains();
        // A new NES instance means a fresh SpeedConfig at its defaults - reapply the checkbox.
        if (_strictAccuracyCheck.Checked) ApplyStrictAccuracyMode(true);
        nes.RunFrame();
        Present();
        RefreshRegisters();
        SetStatus($"Loaded {name}");
        _playPauseBtn.Text = "Play";
    }

    private void ResetRom()
    {
        if (_romBytes == null) return;
        bool wasRunning = _timer.Enabled;
        LoadRom(_romBytes, _romName);
        if (wasRunning) TogglePlay();
    }

    private void SyncCoreSelectorsFromNes()
    {
        if (_nes == null) return;
        SelectCombo(_cpuCombo, StripPrefix(_nes.GetCpuCoreId(), "CPU_"));
        SelectCombo(_ppuCombo, StripPrefix(_nes.GetPpuCoreId(), "PPU_"));
        SelectCombo(_apuCombo, StripPrefix(_nes.GetApuCoreId(), "APU_"));
    }

    private static void SelectCombo(ComboBox combo, string value)
    {
        var idx = combo.Items.IndexOf(value);
        if (idx >= 0) combo.SelectedIndex = idx;
    }

    // NES.Get*CoreId() returns the CLR type name ("CPU_SPD"); CoreRegistry.*Ids and
    // Set*Core() both use the bare suffix ("SPD") - same reconciliation Web/Emulation/
    // Emulator.cs needed.
    private static string StripPrefix(string typeName, string prefix)
        => typeName.StartsWith(prefix, StringComparison.Ordinal) ? typeName[prefix.Length..] : typeName;

    // ---- Transport ----------------------------------------------------------

    private void TogglePlay()
    {
        if (_nes == null) return;
        if (_timer.Enabled)
        {
            _timer.Stop();
            _playPauseBtn.Text = "Play";
        }
        else
        {
            _timer.Start();
            _playPauseBtn.Text = "Pause";
        }
    }

    private void StepFrame()
    {
        if (_nes == null) return;
        _timer.Stop();
        _playPauseBtn.Text = "Play";
        RunOneFrame();
    }

    private void StepInstruction()
    {
        if (_nes == null) return;
        _timer.Stop();
        _playPauseBtn.Text = "Play";
        try
        {
            int cycles = _nes.StepInstruction();
            Present();
            RefreshRegisters();
            SetStatus(_nes.IsCrashed() ? $"CRASHED: {_nes.GetCrashInfo()}" : $"{_romName} - stepped {cycles} cycle(s)");
        }
        catch (Exception ex)
        {
            SetStatus($"Exception: {ex.Message}");
        }
    }

    // See the SpeedConfig comment on NES.GetSpeedConfig(): these shortcuts apply identically
    // under every core combination, so leaving them on during an accuracy comparison risks
    // misattributing a global speed-hack artifact to whichever core happened to be selected.
    private void ApplyStrictAccuracyMode(bool strict)
    {
        var cfg = _nes?.GetSpeedConfig();
        if (cfg == null) return;
        bool value = !strict;
        cfg.CpuFastOamDmaStall = value;
        cfg.CpuIdleLoopDetect = value;
        cfg.CpuIdleLoopSkip = value;
        cfg.CpuIdleLoopSkipApuStatus = value;
        cfg.CpuAdaptiveBatching = value;
        cfg.PpuSkipBlankScanlines = value;
        cfg.PpuUnsafeScanline = value;
        cfg.PpuDeferAttributeFetch = value;
        SetStatus(strict ? "Strict accuracy mode ON (speed hacks disabled)" : "Strict accuracy mode OFF (default speed hacks)");
    }

    private void Timer_Tick(object? sender, EventArgs e) => RunOneFrame();

    private void RunOneFrame()
    {
        if (_nes == null) return;
        try
        {
            if (_selfPlayCheck.Checked && _selfPlayManager != null)
            {
                // _p1 (live keyboard state) is passed through as the human-override input, same
                // merge rule SelfPlayManager already implements: the model may never press Start,
                // a human still can (press Enter to kick off a fresh session, exactly like
                // clicking "Start Autoplay" then pressing Start in the source project's UI).
                var buttons = _selfPlayManager.ComputeFrameInput(_nes, _p1);
                _nes.SetInputs(buttons, null);
                _nes.RunFrame();
                _selfPlayManager.OnFrameComplete(_nes);
            }
            else
            {
                _nes.SetInputs(_p1, _p2);
                _nes.RunFrame();
            }
            _frameCount++;
            Present();
            RefreshRegisters();
            if (_nes.IsCrashed())
            {
                _timer.Stop();
                _playPauseBtn.Text = "Play";
                SetStatus($"CRASHED: {_nes.GetCrashInfo()}");
            }
            else if (_selfPlayCheck.Checked && _selfPlayManager != null)
            {
                SetStatus($"{_romName} - frame {_frameCount} | self-play: " +
                    $"{(_selfPlayManager.IsPipeConnected ? "connected" : "DISCONNECTED")} " +
                    $"style={_selfPlayManager.CurrentStyle} temp={_selfPlayManager.CurrentTemperature:F2} " +
                    $"checkpoints={_selfPlayManager.Checkpoints.Count} reloads={_selfPlayManager.ReloadCount} " +
                    $"blockedStart={_selfPlayManager.BlockedStartCount}");
            }
            else
            {
                SetStatus($"{_romName} - frame {_frameCount}");
            }
        }
        catch (Exception ex)
        {
            _timer.Stop();
            _playPauseBtn.Text = "Play";
            SetStatus($"Exception: {ex.Message}");
        }
    }

    private void ToggleSelfPlay(bool enabled)
    {
        if (!enabled)
        {
            SetStatus("Self-play disabled.");
            return;
        }

        if (!string.Equals(_romName, Path.GetFileName(Smb1RomPath), StringComparison.Ordinal) && File.Exists(Smb1RomPath))
        {
            try { LoadRom(File.ReadAllBytes(Smb1RomPath), Path.GetFileName(Smb1RomPath)); }
            catch (Exception ex)
            {
                SetStatus($"Failed to auto-load SMB1 ROM: {ex.Message}");
                _selfPlayCheck.Checked = false;
                return;
            }
        }

        _selfPlayManager ??= new SelfPlayManager(new SelfPlayConfig(), Path.Combine(Path.GetTempPath(), "brokennes_selfplay_checkpoints"));
        if (!_selfPlayManager.IsPipeConnected && !_selfPlayManager.ConnectPipe("nesreflex_inference"))
        {
            MessageBox.Show(this,
                "Could not connect to the NESReflex inference server pipe.\n\n" +
                "Make sure it's running first:\n" +
                "python scripts/nesreflex_inference_server.py --checkpoint <path> --metadata <path> --protocol v2",
                "Self-play", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (!_timer.Enabled) TogglePlay();
        SetStatus("Self-play enabled. Click the game window and press Enter to start (the model can never press Start itself).");
    }

    private void Present()
    {
        if (_nes == null) return;
        var fb = _nes.GetFrameBuffer();
        if (fb == null || fb.Length != NesWidth * NesHeight * 4) return;
        _directBitmap.CopyFromBytes(fb);
        _screen.Image?.Dispose();
        _screen.Image = _directBitmap.ToBitmap();
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    // ---- Input --------------------------------------------------------------

    // Index order fixed by NesEmulator.Input.SetInput (Windows/NesEmulator/board/Input.cs:10):
    // 0=A 1=B 2=Select 3=Start 4=Up 5=Down 6=Left 7=Right. NES.SetInputs() passes the array
    // straight through with no remapping (NES.cs:1148-1153).
    private void ApplyKey(Keys key, bool down)
    {
        int index = key switch
        {
            Keys.X => 0,
            Keys.Z => 1,
            Keys.Space => 2,
            Keys.Enter => 3,
            Keys.Up or Keys.W => 4,
            Keys.Down or Keys.S => 5,
            Keys.Left or Keys.A => 6,
            Keys.Right or Keys.D => 7,
            _ => -1,
        };
        if (index >= 0) _p1[index] = down;
    }

    // ---- Registers ------------------------------------------------------------

    private void RefreshRegisters()
    {
        if (_nes == null) { _registersBox.Text = "(no ROM loaded)"; return; }
        var r = _nes.GetCpuRegs();
        _registersBox.Text =
            $"PC={r.PC:X4}  SP={r.SP:X4}\r\n" +
            $"A={r.A:X2}  X={r.X:X2}  Y={r.Y:X2}\r\n" +
            $"P={r.P:X2} ({FormatFlags(r.P)})";
    }

    private static string FormatFlags(byte p)
    {
        char[] names = { 'C', 'Z', 'I', 'D', 'B', '-', 'V', 'N' };
        var chars = new char[8];
        for (int i = 0; i < 8; i++)
            chars[7 - i] = (p & (1 << i)) != 0 ? names[i] : '.';
        return new string(chars);
    }

    // ---- Memory viewer ----------------------------------------------------------

    private void PopulateMemoryDomains()
    {
        _memDomainCombo.Items.Clear();
        if (_nes == null) return;
        foreach (var d in _nes.GetAvailableMemoryDomains())
            _memDomainCombo.Items.Add(d.Name);
        if (_memDomainCombo.Items.Count > 0) _memDomainCombo.SelectedIndex = 0;
    }

    private void RefreshMemoryView()
    {
        if (_nes == null || _memDomainCombo.SelectedItem is not string domain) { _memDumpBox.Text = string.Empty; return; }
        if (!int.TryParse(_memAddressBox.Text, System.Globalization.NumberStyles.HexNumber, null, out int startAddr))
            startAddr = 0;

        int size = _nes.GetMemoryDomainSize(domain);
        int length = Math.Min(256, Math.Max(0, size - startAddr));
        if (length <= 0) { _memDumpBox.Text = "(out of range)"; return; }

        var bytes = _nes.PeekMemoryRange(domain, startAddr, length);
        var sb = new System.Text.StringBuilder();
        for (int row = 0; row < bytes.Length; row += 16)
        {
            sb.Append((startAddr + row).ToString("X4")).Append(":  ");
            int rowLen = Math.Min(16, bytes.Length - row);
            for (int col = 0; col < 16; col++)
                sb.Append(col < rowLen ? bytes[row + col].ToString("X2") : "  ").Append(' ');
            sb.Append(' ');
            for (int col = 0; col < rowLen; col++)
            {
                byte b = bytes[row + col];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.AppendLine();
        }
        _memDumpBox.Text = sb.ToString();
    }

    // ---- Save / load state -------------------------------------------------------

    private void SaveState()
    {
        if (_nes == null) return;
        try
        {
            _savedState = _nes.SaveState();
            SetStatus("State saved (in memory).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadState()
    {
        if (_nes == null || _savedState == null) return;
        try
        {
            _nes.LoadState(_savedState);
            Present();
            RefreshRegisters();
            SetStatus("State restored.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- Benchmarks ----------------------------------------------------------

    private void RunBenchmarks()
    {
        if (_nes == null) return;
        try
        {
            var results = _nes.RunBenchmarks();
            var sb = new System.Text.StringBuilder();
            foreach (var r in results) sb.AppendLine(r.ToString());
            MessageBox.Show(this, sb.ToString(), "Benchmark results", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Benchmark failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
