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
using NesEmulator.Snes;
using SharpDX.XInput;

namespace BrokenNes.Workshop;

/// <summary>
/// Minimal interactive SNES player for the SFC core family: `Workshop --snes game.sfc [--apu SFC|HLE]`.
///
/// Emulation runs on its own thread. When the audio unit produces samples, the audio device's
/// clock paces the emulator (keep ~70ms queued), which gives exact speed and no crackle. With a
/// silent unit (APU_HLE) it falls back to a stopwatch at the NTSC frame rate.
///
/// Keys: arrows = D-pad, Z = Y, X = B, A = X, S = A, Q = L, W = R, Enter = Start, Space = Select.
///       P pause, F2 reset, Tab (hold) fast-forward, F12 screenshot, Esc quit. An XInput pad also works.
/// Battery SRAM persists to %APPDATA%\BrokenNes\BatterySaves\sfc&lt;sha1&gt;.srm.
/// </summary>
internal sealed class SnesPlayerForm : Form
{
    private const double NtscFps = 21477272.0 / (1364 * 262);   // ~60.099

    private readonly BOARD_SFC board;
    private readonly string romPath, savePath;
    // Sized for hi-res (512 wide); normal frames use the left 256 columns.
    private readonly Bitmap bitmap = new(PPU_SFC.HiResWidth, PPU_SFC.MaxHeight, PixelFormat.Format32bppRgb);
    private readonly uint[] shown = new uint[PPU_SFC.HiResWidth * PPU_SFC.MaxHeight];
    private int shownWidth = PPU_SFC.Width;
    private readonly object frameLock = new();
    private int shownHeight = PPU_SFC.Height;
    private int paintPending;

    private readonly Thread emuThread;
    private volatile bool running = true, paused, fastForward, resetRequested, screenshotRequested;
    private volatile ushort keyboardPad;
    private readonly Controller pad = new(UserIndex.One);

    private WaveOutEvent? waveOut;
    private BufferedWaveProvider? audio;
    private double fps;
    private byte[] lastSavedSram;

    public SnesPlayerForm(string romPath, string apuChoice)
    {
        this.romPath = romPath;
        byte[] file = File.ReadAllBytes(romPath);
        var cart = SnesCartridge.Load(file);
        board = new BOARD_SFC(cart, CreateApu(apuChoice), SnesFirmware.CreateCoprocessor(cart, romPath, out string chipNote));

        string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes", "BatterySaves");
        savePath = Path.Combine(saveDir, "sfc" + Convert.ToHexString(SHA1.HashData(file)).ToLowerInvariant() + ".srm");
        if (cart.Sram.Length > 0 && File.Exists(savePath))
        {
            byte[] saved = File.ReadAllBytes(savePath);
            Array.Copy(saved, cart.Sram, Math.Min(saved.Length, cart.Sram.Length));
        }
        lastSavedSram = (byte[])cart.Sram.Clone();

        Text = $"BrokenNes SFC - {cart.Title} [{board.Apu.CoreName}]" + (board.Coprocessor != null ? $" [{board.Coprocessor.Name}]" : chipNote != "" ? $" ({chipNote})" : "");
        ClientSize = new Size(PPU_SFC.Width * 3, PPU_SFC.Height * 3);
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
        catch { audio = null; waveOut = null; }   // no audio device: run silent, stopwatch-paced

        emuThread = new Thread(EmulationLoop) { IsBackground = true, Name = "SFC emulation" };
        emuThread.Start();
    }

    private static ISnesApu CreateApu(string choice) => SnesApuChoice.Create(choice);

    // =====================================================================================
    //  Emulation thread
    // =====================================================================================

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private void EmulationLoop()
    {
        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            double ticksPerFrame = Stopwatch.Frequency / NtscFps;
            double next = sw.ElapsedTicks;
            var samples = new short[8192];
            var bytes = new byte[samples.Length * 2];
            bool audioLive = false;
            int framesThisSecond = 0;
            long secondStart = sw.ElapsedTicks;

            while (running)
            {
                if (resetRequested) { resetRequested = false; board.Reset(); }
                if (paused) { Thread.Sleep(10); next = sw.ElapsedTicks; continue; }

                board.Pads[0] = (ushort)(keyboardPad | PollGamepad());
                board.RunFrame();
                PublishFrame();

                int n;
                while ((n = board.Apu.ReadSamples(samples)) > 0)
                {
                    audioLive = true;
                    if (audio == null || fastForward) continue;
                    Buffer.BlockCopy(samples, 0, bytes, 0, n * 2);
                    audio.AddSamples(bytes, 0, n * 2);
                }

                if (board.FrameCount % 600 == 0) PersistSram();

                if (!fastForward)
                {
                    if (audioLive && audio != null)
                    {
                        while (running && audio.BufferedDuration.TotalMilliseconds > 70) Thread.Sleep(1);
                    }
                    else
                    {
                        next += ticksPerFrame;
                        if (sw.ElapsedTicks - next > ticksPerFrame * 5) next = sw.ElapsedTicks;   // fell far behind: resync
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
            BeginInvoke(() => MessageBox.Show(this, ex.ToString(), "SFC emulation crashed", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        finally { timeEndPeriod(1); }
    }

    private void PublishFrame()
    {
        lock (frameLock)
        {
            shownHeight = board.Ppu.VisibleHeight;
            bool hires = board.Ppu.FrameHasHiRes;   // modes 5/6: show all 512 pixels (sharp hi-res text)
            shownWidth = hires ? PPU_SFC.HiResWidth : PPU_SFC.Width;
            Array.Copy(hires ? board.Ppu.GetHiResFrame() : board.Ppu.FrameBuffer, shown, shownWidth * shownHeight);
        }
        if (screenshotRequested) { screenshotRequested = false; SaveScreenshot(); }
        if (Interlocked.Exchange(ref paintPending, 1) == 0)
        {
            try { BeginInvoke(() => { paintPending = 0; UpdateTitle(); Invalidate(); }); }
            catch (InvalidOperationException) { paintPending = 0; }   // window closing
        }
    }

    private ushort PollGamepad()
    {
        if (!pad.IsConnected) return 0;
        Gamepad g;
        try { g = pad.GetState().Gamepad; } catch { return 0; }
        ushort b = 0;
        var btn = g.Buttons;
        const short dead = 12000;
        if ((btn & GamepadButtonFlags.DPadUp) != 0 || g.LeftThumbY > dead) b |= (ushort)SnesButtons.Up;
        if ((btn & GamepadButtonFlags.DPadDown) != 0 || g.LeftThumbY < -dead) b |= (ushort)SnesButtons.Down;
        if ((btn & GamepadButtonFlags.DPadLeft) != 0 || g.LeftThumbX < -dead) b |= (ushort)SnesButtons.Left;
        if ((btn & GamepadButtonFlags.DPadRight) != 0 || g.LeftThumbX > dead) b |= (ushort)SnesButtons.Right;
        // Positional mapping: the Xbox face buttons sit where the SNES ones do.
        if ((btn & GamepadButtonFlags.A) != 0) b |= (ushort)SnesButtons.B;
        if ((btn & GamepadButtonFlags.B) != 0) b |= (ushort)SnesButtons.A;
        if ((btn & GamepadButtonFlags.X) != 0) b |= (ushort)SnesButtons.Y;
        if ((btn & GamepadButtonFlags.Y) != 0) b |= (ushort)SnesButtons.X;
        if ((btn & GamepadButtonFlags.LeftShoulder) != 0) b |= (ushort)SnesButtons.L;
        if ((btn & GamepadButtonFlags.RightShoulder) != 0) b |= (ushort)SnesButtons.R;
        if ((btn & GamepadButtonFlags.Start) != 0) b |= (ushort)SnesButtons.Start;
        if ((btn & GamepadButtonFlags.Back) != 0) b |= (ushort)SnesButtons.Select;
        return b;
    }

    private void PersistSram()
    {
        var sram = board.Cart.Sram;
        if (sram.Length == 0 || sram.AsSpan().SequenceEqual(lastSavedSram)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            File.WriteAllBytes(savePath, sram);
            lastSavedSram = (byte[])sram.Clone();
        }
        catch { /* best effort; retried on the next interval and on close */ }
    }

    private void SaveScreenshot()
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(romPath)) ?? ".";
        string path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(romPath)}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        lock (frameLock) { using var b = CopyToBitmap(); b.Save(path, ImageFormat.Png); }
    }

    // =====================================================================================
    //  UI thread
    // =====================================================================================

    private Bitmap CopyToBitmap()
    {
        var b = new Bitmap(shownWidth, shownHeight, PixelFormat.Format32bppRgb);
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, b.PixelFormat);
        for (int y = 0; y < shownHeight; y++)
            Marshal.Copy((int[])(object)shown, y * shownWidth, data.Scan0 + y * data.Stride, shownWidth);
        b.UnlockBits(data);
        return b;
    }

    private void UpdateTitle()
    {
        string state = paused ? " PAUSED" : fastForward ? " >>" : "";
        Text = $"BrokenNes SFC - {board.Cart.Title} [{board.Apu.CoreName}] {fps:F1} fps{state}";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int h, sw;
        lock (frameLock)
        {
            h = shownHeight; sw = shownWidth;
            var data = bitmap.LockBits(new Rectangle(0, 0, sw, h), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            for (int y = 0; y < h; y++)
                Marshal.Copy((int[])(object)shown, y * sw, data.Scan0 + y * data.Stride, sw);
            bitmap.UnlockBits(data);
        }
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        float scale = Math.Min(ClientSize.Width / (float)PPU_SFC.Width, ClientSize.Height / (float)h);
        int w = (int)(PPU_SFC.Width * scale), hh = (int)(h * scale);
        g.DrawImage(bitmap, new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - hh) / 2, w, hh),
            new Rectangle(0, 0, sw, h), GraphicsUnit.Pixel);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Color.Black);
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    private static SnesButtons MapKey(Keys k) => k switch
    {
        Keys.Up => SnesButtons.Up, Keys.Down => SnesButtons.Down, Keys.Left => SnesButtons.Left, Keys.Right => SnesButtons.Right,
        Keys.Z => SnesButtons.Y, Keys.X => SnesButtons.B, Keys.A => SnesButtons.X, Keys.S => SnesButtons.A,
        Keys.Q => SnesButtons.L, Keys.W => SnesButtons.R, Keys.Enter => SnesButtons.Start,
        Keys.Space => SnesButtons.Select,
        _ => SnesButtons.None,
    };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Arrow keys and Tab would otherwise be eaten by WinForms focus navigation.
        const int WM_KEYDOWN = 0x100;
        if (msg.Msg == WM_KEYDOWN && (keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Tab))
        {
            OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
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
        var b = MapKey(e.KeyCode);
        if (b != SnesButtons.None) keyboardPad |= (ushort)b;
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Tab) { fastForward = false; return; }
        var b = MapKey(e.KeyCode);
        if (b != SnesButtons.None) keyboardPad &= (ushort)~(ushort)b;
    }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); keyboardPad = 0; fastForward = false; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        running = false;
        emuThread.Join(2000);
        PersistSram();
        waveOut?.Stop();
        waveOut?.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>Entry point for `--snes`.</summary>
    public static int Run(string[] args)
    {
        string? rom = null; string apu = SnesApuChoice.Default;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--apu" && i + 1 < args.Length) apu = args[++i];
            else rom ??= args[i];
        }
        ApplicationConfiguration.Initialize();
        if (rom == null)
        {
            using var dlg = new OpenFileDialog { Filter = "SNES ROMs|*.sfc;*.smc|All files|*.*", Title = "Open a SNES ROM" };
            if (dlg.ShowDialog() != DialogResult.OK) return 2;
            rom = dlg.FileName;
        }
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "SFC player crashed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new SnesPlayerForm(rom, apu));
        return 0;
    }
}
