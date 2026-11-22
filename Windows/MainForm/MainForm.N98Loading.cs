using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    // nano98 loading screen (2026-11-21): opening a .n98 unpacks its game drive and loads the shared warm-boot state, a few seconds of work.
    // It runs off the UI thread; the game area shows the nano98 look meanwhile (black, white text like the boot screen, a progress bar for the
    // unpack, a spinner for the stages without a measurable size), and the window stays responsive.
    public partial class MainForm
    {
        private N98LoadingPanel? n98Loading;
        private int n98LoadGeneration;

        /// <summary>A .n98 opened from disk: its bytes are read off the UI thread (a package can be over a GB) behind the loading screen.</summary>
        private async void LoadN98File(string path)
        {
            int gen = ++n98LoadGeneration;
            NesEmulator.X86.N98WarmPrebuild.Remember(path);
            ShowN98Loading(System.IO.Path.GetFileNameWithoutExtension(path));
            NesEmulator.X86.N98Seat.Report("reading the package", -1);
            byte[] data;
            try { data = await Task.Run(() => System.IO.File.ReadAllBytes(path)); }
            catch (Exception ex)
            {
                if (gen == n98LoadGeneration) HideN98Loading();
                MessageBox.Show(this, $"Error loading {path}: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (gen != n98LoadGeneration || IsDisposed) return;
            try { LoadRomBytes(System.IO.Path.GetFileName(path), data, path, addToRecentRoms: true); }
            catch (Exception ex) { HideN98Loading(); MessageBox.Show(this, $"Error loading ROM: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private async void StartN98Session(byte[] rom, string romName, string? romPath)
        {
            int gen = ++n98LoadGeneration;
            string title = System.IO.Path.GetFileNameWithoutExtension(romName);
            ShowN98Loading(title);
            NesEmulator.X86.N98Seat.Report("opening the package", -1);
            IConsoleSession s;
            try
            {
                string cpu = SelectedCore(ConsoleKind.Pc, CoreSlot.Cpu), ppu = SelectedCore(ConsoleKind.Pc, CoreSlot.Ppu), apu = SelectedCore(ConsoleKind.Pc, CoreSlot.Apu);
                var machine = config.PcMachine; var sega = SegaOptionsFromConfig();
                s = await Task.Run(() => ConsoleSessions.Create(ConsoleKind.Pc, rom, cpu, ppu, apu, names => FindSnesFirmware(romPath, names), romName, sega, machine));
            }
            catch (Exception ex)
            {
                if (gen != n98LoadGeneration) return;
                HideN98Loading();
                MessageBox.Show(this, $"Could not start {title}: {ex.Message}", "BrokenNes 2", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LoadEmbeddedRom(allowHomeWebModule: false);
                return;
            }
            if (gen != n98LoadGeneration || IsDisposed) { s.Dispose(); return; }   // another game was opened meanwhile
            FinishStartSession(ConsoleKind.Pc, s, rom, romName, romPath, start: false);   // (the saves layer goes in before the power-on)
            try
            {
                if (s is NesEmulator.X86.PcSession pc) await Task.Run(() => { lock (emulationLock) pc.Prestart(); });   // warm-boot state + game drive
            }
            catch (Exception ex) { Console.WriteLine($"[nano98] prestart: {ex.Message}"); }
            if (gen != n98LoadGeneration || !ReferenceEquals(session, s)) return;
            HideN98Loading();
            StartEmulation();
        }

        private void ShowN98Loading(string title)
        {
            if (displayPanel == null) return;
            if (n98Loading == null)
            {
                n98Loading = new N98LoadingPanel { Dock = DockStyle.Fill };
                displayPanel.Controls.Add(n98Loading);
            }
            n98Loading.Title = title;
            n98Loading.Visible = true;
            n98Loading.BringToFront();
            n98Loading.Start();
        }

        private void HideN98Loading()
        {
            if (n98Loading == null) return;
            n98Loading.Stop();
            n98Loading.Visible = false;
        }
    }

    /// <summary>The nano98 loading screen: black, "nano98 is loading..." in white, the game's title, the stage, and a bar (or a spinner).</summary>
    internal sealed class N98LoadingPanel : Panel
    {
        public string Title = "";
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
        private int _tick;
        private static readonly Font Big = new("Consolas", 14f, FontStyle.Bold), Small = new("Consolas", 10f);

        public N98LoadingPanel()
        {
            DoubleBuffered = true; BackColor = Color.Black; ForeColor = Color.White;
            _timer.Tick += (_, _) => { _tick++; Invalidate(); };
        }
        public void Start() { _tick = 0; _timer.Start(); Invalidate(); }
        public void Stop() => _timer.Stop();

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Color.Black);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var (stage, frac) = NesEmulator.X86.N98Seat.Progress;
            int w = ClientSize.Width, h = ClientSize.Height, bw = Math.Min(360, w - 40), bh = 10, x = (w - bw) / 2, y = h / 2;
            using var white = new SolidBrush(Color.White); using var grey = new SolidBrush(Color.FromArgb(150, 150, 150)); using var pen = new Pen(Color.White, 1);
            void Center(string t, Font f, Brush b, int yy) { var sz = g.MeasureString(t, f); g.DrawString(t, f, b, (w - sz.Width) / 2, yy); }
            Center("nano98 is loading...", Big, white, y - 64);
            if (Title.Length > 0) Center(Title, Small, grey, y - 34);
            g.DrawRectangle(pen, x, y, bw, bh);
            if (frac >= 0) g.FillRectangle(white, x + 2, y + 2, (int)((bw - 3) * Math.Clamp(frac, 0, 1)), bh - 3);
            else
            {
                int seg = bw / 5, pos = (_tick * 8) % (bw + seg) - seg;   // an indeterminate bar: a block sliding across
                int a = Math.Max(x + 2, x + pos), b = Math.Min(x + bw - 1, x + pos + seg);
                if (b > a) g.FillRectangle(white, a, y + 2, b - a, bh - 3);
            }
            if (stage.Length > 0) Center(stage + (frac >= 0 ? $"  {frac * 100:F0}%" : ""), Small, grey, y + 20);
        }

        protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
    }
}
