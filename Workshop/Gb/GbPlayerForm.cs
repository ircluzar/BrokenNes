using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NesEmulator.Gb;
using SharpDX.XInput;

namespace BrokenNes.Workshop.Gb;

/// <summary>
/// Minimal interactive Game Boy player: `Workshop --gb game.gb|.gbc|.zip [--model dmg|cgb]`.
/// Default model: Game Boy Color for colour games, original Game Boy otherwise.
///
/// Emulation runs on its own thread, paced by the audio device (keep ~70 ms queued).
/// Keys: arrows = D-pad, X = A, Z = B, Enter = Start, Space/Backspace = Select; I/J/K/L tilt MBC7 carts (right stick on a pad).
///       P pause, F2 reset, Tab (hold) fast-forward, F12 screenshot, Esc quit. An XInput pad also works.
/// Battery saves persist to %APPDATA%\BrokenNes\GbSaves\&lt;sha1&gt;.sav (BGB/VBA-compatible, RTC trailer included).
/// </summary>
internal sealed class GbPlayerForm : Form
{
    private readonly BOARD_GB board;
    private readonly string romPath, savePath;
    private readonly Bitmap bitmap = new(PPU_GB.Width, PPU_GB.Height, PixelFormat.Format32bppRgb);
    private readonly uint[] shown = new uint[PPU_GB.Width * PPU_GB.Height];
    private readonly object frameLock = new();
    private int paintPending;

    private readonly Thread emuThread;
    private volatile bool running = true, paused, fastForward, resetRequested, screenshotRequested;
    private volatile byte keyboardPad;
    private volatile int tiltKeys;   // bit 0 up, 1 left, 2 down, 3 right
    private readonly Controller pad = new(UserIndex.One);

    private WaveOutEvent? waveOut;
    private BufferedWaveProvider? audio;
    private double fps;
    private byte[] lastSaved = Array.Empty<byte>();

    public GbPlayerForm(string romPath, string? modelChoice)
    {
        this.romPath = romPath;
        byte[] file = GbRunCli.LoadRom(romPath);
        var cart = GbCartridge.Load(file);
        var model = modelChoice switch
        {
            "dmg" => GbModel.Dmg, "cgb" => GbModel.Cgb,
            _ => cart.SupportsCgb ? GbModel.Cgb : GbModel.Dmg,
        };
        board = new BOARD_GB(cart, model);

        string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes", "GbSaves");
        savePath = Path.Combine(saveDir, Convert.ToHexString(SHA1.HashData(file)).ToLowerInvariant() + ".sav");
        if (board.HasBattery && File.Exists(savePath)) board.ImportSave(File.ReadAllBytes(savePath));
        if (board.HasBattery) lastSaved = board.ExportSave();

        Text = $"BrokenNes GB - {cart.Title} [{cart.MapperName}, {model}]";
        ClientSize = new Size(PPU_GB.Width * 4, PPU_GB.Height * 4);
        DoubleBuffered = true;
        BackColor = Color.Black;
        KeyPreview = true;

        try
        {
            audio = new BufferedWaveProvider(new WaveFormat(board.Apu.SampleRate, 16, 2))
            { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };
            waveOut = new WaveOutEvent { DesiredLatency = 60 };
            waveOut.Init(audio);
            waveOut.Play();
        }
        catch { audio = null; waveOut = null; }

        emuThread = new Thread(EmulationLoop) { IsBackground = true, Name = "GB emulation" };
        emuThread.Start();
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private void EmulationLoop()
    {
        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            double ticksPerFrame = Stopwatch.Frequency / BOARD_GB.DmgFps;
            double next = sw.ElapsedTicks;
            var samples = new short[16384];
            var bytes = new byte[samples.Length * 2];
            int framesThisSecond = 0;
            long secondStart = sw.ElapsedTicks;

            while (running)
            {
                if (resetRequested) { resetRequested = false; board.Reset(); }
                if (paused) { Thread.Sleep(10); next = sw.ElapsedTicks; continue; }

                var buttons = (GbButtons)(keyboardPad | PollGamepad());
                if (buttons != board.Buttons) { board.Buttons = buttons; board.UpdateJoypadIrq(); }
                if (board.Cart.HasTilt) UpdateTilt();
                board.RunFrame();
                PublishFrame();

                int n;
                while ((n = board.Apu.ReadSamples(samples)) > 0)
                {
                    if (audio == null || fastForward) continue;
                    Buffer.BlockCopy(samples, 0, bytes, 0, n * 2);
                    audio.AddSamples(bytes, 0, n * 2);
                }

                if (board.FrameCount % 600 == 0) PersistSave();

                if (!fastForward)
                {
                    if (audio != null)
                    {
                        while (running && audio.BufferedDuration.TotalMilliseconds > 70) Thread.Sleep(1);
                    }
                    else
                    {
                        next += ticksPerFrame;
                        if (sw.ElapsedTicks - next > ticksPerFrame * 5) next = sw.ElapsedTicks;
                        while (running && sw.ElapsedTicks < next) Thread.Sleep(1);
                    }
                }

                framesThisSecond++;
                if (sw.ElapsedTicks - secondStart >= Stopwatch.Frequency)
                {
                    fps = framesThisSecond * (double)Stopwatch.Frequency / (sw.ElapsedTicks - secondStart);
                    framesThisSecond = 0;
                    secondStart = sw.ElapsedTicks;
                }
            }
        }
        catch (Exception ex)
        {
            running = false;
            BeginInvoke(() => MessageBox.Show(this, ex.ToString(), "GB emulation crashed", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        finally { timeEndPeriod(1); }
    }

    private void PublishFrame()
    {
        lock (frameLock) Array.Copy(board.Ppu.FrameBuffer, shown, shown.Length);
        if (screenshotRequested) { screenshotRequested = false; SaveScreenshot(); }
        if (Interlocked.Exchange(ref paintPending, 1) == 0)
        {
            try { BeginInvoke(() => { paintPending = 0; UpdateTitle(); Invalidate(); }); }
            catch (InvalidOperationException) { paintPending = 0; }
        }
    }

    private byte PollGamepad()
    {
        if (!pad.IsConnected) return 0;
        Gamepad g;
        try { g = pad.GetState().Gamepad; } catch { return 0; }
        GbButtons b = 0;
        var btn = g.Buttons;
        const short dead = 12000;
        if ((btn & GamepadButtonFlags.DPadUp) != 0 || g.LeftThumbY > dead) b |= GbButtons.Up;
        if ((btn & GamepadButtonFlags.DPadDown) != 0 || g.LeftThumbY < -dead) b |= GbButtons.Down;
        if ((btn & GamepadButtonFlags.DPadLeft) != 0 || g.LeftThumbX < -dead) b |= GbButtons.Left;
        if ((btn & GamepadButtonFlags.DPadRight) != 0 || g.LeftThumbX > dead) b |= GbButtons.Right;
        if ((btn & GamepadButtonFlags.B) != 0) b |= GbButtons.A;
        if ((btn & GamepadButtonFlags.A) != 0) b |= GbButtons.B;
        if ((btn & GamepadButtonFlags.Start) != 0) b |= GbButtons.Start;
        if ((btn & GamepadButtonFlags.Back) != 0) b |= GbButtons.Select;
        return (byte)b;
    }

    private void PersistSave()
    {
        if (!board.HasBattery) return;
        var data = board.ExportSave();
        int cmp = board.Cart.Ram.Length;   // the RTC trailer's timestamp always changes; compare RAM only
        if (lastSaved.Length >= cmp && data.AsSpan(0, cmp).SequenceEqual(lastSaved.AsSpan(0, cmp)) && board.Cart.Rtc == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            File.WriteAllBytes(savePath, data);
            lastSaved = data;
        }
        catch { }
    }

    private void SaveScreenshot()
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(romPath)) ?? ".";
        string path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(romPath)}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        lock (frameLock) { using var b = CopyToBitmap(); b.Save(path, ImageFormat.Png); }
    }

    private Bitmap CopyToBitmap()
    {
        var b = new Bitmap(PPU_GB.Width, PPU_GB.Height, PixelFormat.Format32bppRgb);
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, b.PixelFormat);
        Marshal.Copy((int[])(object)shown, 0, data.Scan0, shown.Length);
        b.UnlockBits(data);
        return b;
    }

    private void UpdateTitle()
    {
        string state = paused ? " PAUSED" : fastForward ? " >>" : "";
        Text = $"BrokenNes GB - {board.Cart.Title} [{board.Cart.MapperName}, {board.Model}] {fps:F1} fps{state}";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        lock (frameLock)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, PPU_GB.Width, PPU_GB.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            Marshal.Copy((int[])(object)shown, 0, data.Scan0, shown.Length);
            bitmap.UnlockBits(data);
        }
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        float scale = Math.Min(ClientSize.Width / (float)PPU_GB.Width, ClientSize.Height / (float)PPU_GB.Height);
        int w = (int)(PPU_GB.Width * scale), h = (int)(PPU_GB.Height * scale);
        g.DrawImage(bitmap, new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h),
            new Rectangle(0, 0, PPU_GB.Width, PPU_GB.Height), GraphicsUnit.Pixel);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Color.Black);
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    private static GbButtons MapKey(Keys k) => k switch
    {
        Keys.Up => GbButtons.Up, Keys.Down => GbButtons.Down, Keys.Left => GbButtons.Left, Keys.Right => GbButtons.Right,
        Keys.X => GbButtons.A, Keys.Z => GbButtons.B, Keys.Enter => GbButtons.Start,
        Keys.Space or Keys.Back => GbButtons.Select,
        _ => 0,
    };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        const int WM_KEYDOWN = 0x100;
        if (msg.Msg == WM_KEYDOWN && (keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Tab))
        {
            OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static int TiltBit(Keys k) => k switch { Keys.I => 1, Keys.J => 2, Keys.K => 4, Keys.L => 8, _ => 0 };

    /// <summary>Accelerometer for MBC7 carts: keys give a half-g tilt, the right stick is analogue.</summary>
    private void UpdateTilt()
    {
        int t = tiltKeys;
        float x = ((t & 8) != 0 ? 0.5f : 0) - ((t & 2) != 0 ? 0.5f : 0), y = ((t & 4) != 0 ? 0.5f : 0) - ((t & 1) != 0 ? 0.5f : 0);
        if (pad.IsConnected)
        {
            try
            {
                var g = pad.GetState().Gamepad;
                if (Math.Abs((int)g.RightThumbX) > 6000) x = g.RightThumbX / 32768f;
                if (Math.Abs((int)g.RightThumbY) > 6000) y = -g.RightThumbY / 32768f;
            }
            catch { }
        }
        board.Cart.SetTilt(x, y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape: Close(); return;
            case Keys.P: paused = !paused; UpdateTitle(); return;
            case Keys.F2: resetRequested = true; return;
            case Keys.F12: screenshotRequested = true; return;
            case Keys.Tab: fastForward = true; return;
        }
        keyboardPad |= (byte)MapKey(e.KeyCode);
        tiltKeys |= TiltBit(e.KeyCode);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Tab) { fastForward = false; return; }
        keyboardPad &= (byte)~(byte)MapKey(e.KeyCode);
        tiltKeys &= ~TiltBit(e.KeyCode);
    }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); keyboardPad = 0; fastForward = false; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        running = false;
        emuThread.Join(2000);
        PersistSave();
        waveOut?.Stop();
        waveOut?.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>Entry point for `--gb`.</summary>
    public static int Run(string[] args)
    {
        string? rom = null, model = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--model" && i + 1 < args.Length) model = args[++i].ToLowerInvariant();
            else rom ??= args[i];
        }
        ApplicationConfiguration.Initialize();
        if (rom == null)
        {
            using var dlg = new OpenFileDialog { Filter = "Game Boy ROMs|*.gb;*.gbc;*.zip|All files|*.*", Title = "Open a Game Boy ROM" };
            if (dlg.ShowDialog() != DialogResult.OK) return 2;
            rom = dlg.FileName;
        }
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "GB player crashed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new GbPlayerForm(rom, model));
        return 0;
    }
}
