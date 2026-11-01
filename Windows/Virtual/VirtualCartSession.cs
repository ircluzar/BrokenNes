using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NesEmulator.Systems;

namespace BrokenNes.Windows.Virtual
{
    /// <summary>
    /// WideMess Virtual (docs/projects/next-pool-plans.md, P4): a web game as a WideMess cartridge. The game's own server runs (<see cref="VirtualCartServer"/>), its page
    /// runs in an offscreen WebView2 on a thread of its own, the injected compositor (<see cref="VirtualCartScript"/>) hands each frame's pixels over a shared buffer,
    /// and this session shows them in the middle of the WideMess's 400 x 224 picture (a black column each side) through the app's normal display path. The pad goes to the
    /// page as a standard gamepad; the page plays its own sound, at BrokenNes's volume. Nothing is emulated, so there is no machine state: no save states, rewind,
    /// Timeflow, core swaps or corruption, and each says so (<see cref="NoMachineReason"/>).
    /// </summary>
    public sealed class VirtualCartSession : IConsoleSession
    {
        public const string NoMachineReason = "A WideMess virtual cartridge is a web game shown through BrokenNes, not an emulated machine: there is no machine state to save, rewind, record, corrupt or move to another core.";

        private const int W = VirtualCartManifest.ConsoleWidth, H = VirtualCartManifest.ConsoleHeight;
        private const int Rate = 48000;

        private readonly VirtualCartManifest manifest;
        private readonly VirtualCartServer server;
        private readonly Thread hostThread;
        private readonly ManualResetEventSlim hostReady = new(false);
        private Form? hostForm;
        private WebView2? view;
        private CoreWebView2SharedBuffer? shared;
        private readonly object frameLock = new();
        private readonly uint[] latest = new uint[W * H];
        private readonly uint[] frame = new uint[W * H];
        private bool latestNew;
        private int disposed;

        // pad, volume, pause (the emulation thread writes, the host thread applies)
        private PadButtons pad;
        private int padSentMask = -1, padSeq;
        private float sentVolume = -1; private bool sentMuted;
        private long lastRunFrameTicks;
        private volatile bool pagePaused;
        private double sampleDebt;
        private int pendingShorts;

        // what the check and the status read
        private long pageFrames, framesShown, framesRepeated, framesOverwritten, pageErrors;
        private string lastPageError = "";
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly long[] padSentAt = new long[256];
        private double lastPadEchoMs = -1;

        public VirtualCartManifest Manifest => manifest;
        public int ServerProcessId => server.ProcessId;
        public string ServerUrl => server.BaseUrl;
        /// <summary>Null while everything runs; what went wrong otherwise (the server stopped, the page's process failed, the web view could not start).</summary>
        public string? Problem => server.Exited ?? hostProblem;
        private volatile string? hostProblem;
        public long PageFrames => Interlocked.Read(ref pageFrames);
        public long FramesShown => Interlocked.Read(ref framesShown);
        public long FramesRepeated => Interlocked.Read(ref framesRepeated);
        public long FramesOverwritten => Interlocked.Read(ref framesOverwritten);
        public string LastPageError => lastPageError;
        public long PageErrors => Interlocked.Read(ref pageErrors);
        public bool PagePaused => pagePaused;
        /// <summary>The last time from handing the page a pad change to the page reporting it in a frame (ms), -1 before any.</summary>
        public double LastPadEchoMs => lastPadEchoMs;

        /// <summary>Starts the server and the page. Throws <see cref="InvalidOperationException"/> / <see cref="InvalidDataException"/> with a message for the user.</summary>
        public VirtualCartSession(VirtualCartManifest m, string webViewDataFolder)
        {
            manifest = m;
            Title = m.Title;
            GameId = "wmv" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(m.Folder.ToLowerInvariant()))).ToLowerInvariant()[..37];
            for (int i = 0; i < frame.Length; i++) frame[i] = latest[i] = 0xFF000000;
            server = VirtualCartServer.Start(m, TimeSpan.FromSeconds(30));
            hostThread = new Thread(() => HostMain(webViewDataFolder)) { IsBackground = true, Name = "WideMess Virtual host" };
            hostThread.SetApartmentState(ApartmentState.STA);
            hostThread.Start();
            if (!hostReady.Wait(TimeSpan.FromSeconds(30)) || hostProblem != null)
            {
                string why = hostProblem ?? "the web view did not start within 30 s";
                Dispose();
                throw new InvalidOperationException($"Could not open {m.Title}: {why}");
            }
        }

        // ------------------------------------------------------------------ the host thread: an offscreen window with the web view
        private void HostMain(string dataFolder)
        {
            try
            {
                hostForm = new OffscreenForm(manifest.Width, manifest.Height);
                hostForm.Load += async (_, _) =>
                {
                    try { await InitWebView(dataFolder); }
                    catch (Exception ex) { hostProblem = $"the web view could not start: {ex.Message}"; }
                    finally { hostReady.Set(); }
                };
                var pauseTimer = new System.Windows.Forms.Timer { Interval = 50 };
                pauseTimer.Tick += (_, _) => WatchPause();
                pauseTimer.Start();
                Application.Run(hostForm);
                pauseTimer.Dispose();
            }
            catch (Exception ex) { hostProblem = $"the web view's thread failed: {ex.Message}"; hostReady.Set(); }
        }

        private async Task InitWebView(string dataFolder)
        {
            Directory.CreateDirectory(dataFolder);
            var options = new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling --disable-renderer-backgrounding " +
                "--disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion");
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder, options);
            view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
            hostForm!.Controls.Add(view);
            await view.EnsureCoreWebView2Async(env);
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("BROKENNES_WMV_DEVTOOLS") == "1";
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(VirtualCartScript.For(manifest.Width, manifest.Height, manifest.Layers));
            shared = env.CreateSharedBuffer((ulong)(2 * manifest.Width * manifest.Height * 4));
            core.WebMessageReceived += OnPageMessage;
            core.DOMContentLoaded += (_, _) => { try { core.PostSharedBufferToScript(shared, CoreWebView2SharedBufferAccess.ReadWrite, null); padSentMask = -1; sentVolume = -1; } catch (Exception ex) { hostProblem = $"the frame buffer could not be shared: {ex.Message}"; } };
            core.ProcessFailed += (_, e) => hostProblem = $"the page's {e.ProcessFailedKind} failed ({e.Reason})";
            core.NewWindowRequested += (_, e) => e.Handled = true;   // the game stays in its one page
            core.Navigate(server.BaseUrl + manifest.Page);
        }

        private unsafe void OnPageMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string? msg;
            try { msg = e.TryGetWebMessageAsString(); } catch { return; }
            if (msg == null || msg.Length < 2) return;
            if (msg[0] == 'e') { Interlocked.Increment(ref pageErrors); lastPageError = msg[2..]; return; }
            if (msg[0] != 'f' || shared == null) return;
            var parts = msg.Split(',');
            if (parts.Length < 4 || !int.TryParse(parts[2], out int slot) || !int.TryParse(parts[3], out int seq)) return;
            int gw = manifest.Width, gh = manifest.Height;
            int ox = (W - gw) / 2, oy = (H - gh) / 2;
            byte* src = (byte*)shared.Buffer + (long)slot * gw * gh * 4;
            lock (frameLock)
            {
                for (int y = 0; y < gh; y++)
                {
                    byte* s = src + (long)y * gw * 4;
                    int d = (y + oy) * W + ox;
                    for (int x = 0; x < gw; x++, s += 4) latest[d + x] = 0xFF000000u | (uint)s[0] << 16 | (uint)s[1] << 8 | s[2];
                }
                if (latestNew) Interlocked.Increment(ref framesOverwritten);
                latestNew = true;
            }
            Interlocked.Increment(ref pageFrames);
            if (seq > 0 && padSentAt[seq & 255] != 0) { lastPadEchoMs = (clock.ElapsedTicks - padSentAt[seq & 255]) * 1000.0 / Stopwatch.Frequency; padSentAt[seq & 255] = 0; }
        }

        /// <summary>BrokenNes paused (no frame asked for a while): the page is frozen (TrySuspend) and muted; it comes back on the next frame.</summary>
        private async void WatchPause()
        {
            var core = view?.CoreWebView2;
            if (core == null || Volatile.Read(ref disposed) != 0) return;
            bool idle = Volatile.Read(ref lastRunFrameTicks) != 0 && clock.ElapsedTicks - Volatile.Read(ref lastRunFrameTicks) > Stopwatch.Frequency / 4;
            if (idle && !pagePaused)
            {
                pagePaused = true;
                core.IsMuted = true;
                try { view!.Visible = false; await core.TrySuspendAsync(); } catch { }
            }
            else if (!idle && pagePaused)
            {
                try { core.Resume(); } catch { }
                view!.Visible = true;
                core.IsMuted = sentMuted;
                pagePaused = false;
            }
            SendPadAndVolume(core);
        }

        private void SendPadAndVolume(CoreWebView2 core)
        {
            if (pagePaused) return;
            int mask = StandardMask(pad);
            if (mask != padSentMask)
            {
                padSentMask = mask;
                int seq = ++padSeq;
                padSentAt[seq & 255] = clock.ElapsedTicks;
                core.PostWebMessageAsJson($"{{\"pad\":{mask},\"seq\":{seq}}}");
            }
            float vol = AudioManager.MasterVolume; bool muted = AudioManager.Muted;
            if (vol != sentVolume) { sentVolume = vol; core.PostWebMessageAsJson($"{{\"volume\":{vol.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"); }
            if (muted != sentMuted || core.IsMuted != muted) { sentMuted = muted; core.IsMuted = muted; }
        }

        /// <summary>The twelve-button pad as the standard gamepad mapping: B 0 (bottom), A 1 (right), Y 2 (left), X 3 (top), L 4, R 5, Select 8, Start 9, the d-pad 12-15.</summary>
        internal static int StandardMask(PadButtons p)
        {
            int m = 0;
            if ((p & PadButtons.B) != 0) m |= 1 << 0;
            if ((p & PadButtons.A) != 0) m |= 1 << 1;
            if ((p & PadButtons.Y) != 0) m |= 1 << 2;
            if ((p & PadButtons.X) != 0) m |= 1 << 3;
            if ((p & PadButtons.L) != 0) m |= 1 << 4;
            if ((p & PadButtons.R) != 0) m |= 1 << 5;
            if ((p & PadButtons.Select) != 0) m |= 1 << 8;
            if ((p & PadButtons.Start) != 0) m |= 1 << 9;
            if ((p & PadButtons.Up) != 0) m |= 1 << 12;
            if ((p & PadButtons.Down) != 0) m |= 1 << 13;
            if ((p & PadButtons.Left) != 0) m |= 1 << 14;
            if ((p & PadButtons.Right) != 0) m |= 1 << 15;
            return m;
        }

        private void OnHost(Action a)
        {
            var f = hostForm;
            if (f == null || Volatile.Read(ref disposed) != 0) return;
            try { if (f.IsHandleCreated) f.BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        // ------------------------------------------------------------------ IConsoleSession
        public ConsoleKind Console => ConsoleKind.WideMess;
        public string Title { get; }
        public string Description => Problem is { } p
            ? $"WideMess virtual cartridge {Title}: STOPPED - {p}"
            : $"WideMess virtual cartridge {Title} ({manifest.Width} x {manifest.Height} web game in {W} x {H}; its own server on {server.BaseUrl}; its own sound)";
        public double FramesPerSecond => 60.0;
        public uint[] Frame => frame;
        public int FrameWidth => W;
        public int FrameHeight => H;
        public int DisplayWidth => W;
        public int SampleRate => Rate;
        public bool HasBattery => false;
        public string GameId { get; }

        public void SetPad(int player, PadButtons buttons)
        {
            if (player != 0) return;
            if (buttons != pad) { pad = buttons; OnHost(() => { if (view?.CoreWebView2 is { } c) SendPadAndVolume(c); }); }
        }

        public void RunFrame()
        {
            bool resume = pagePaused;
            Volatile.Write(ref lastRunFrameTicks, clock.ElapsedTicks);
            if (resume) OnHost(WatchPause);
            lock (frameLock)
            {
                if (latestNew) { Array.Copy(latest, frame, frame.Length); latestNew = false; Interlocked.Increment(ref framesShown); }
                else Interlocked.Increment(ref framesRepeated);
            }
            // silence of the right length: BrokenNes paces a session by its sound; the page plays its own
            sampleDebt += Rate / FramesPerSecond;
            int whole = (int)sampleDebt; sampleDebt -= whole;
            pendingShorts += whole * 2;
        }

        public int ReadSamples(short[] buffer)
        {
            int n = Math.Min(pendingShorts, buffer.Length & ~1);
            if (n <= 0) return 0;
            Array.Clear(buffer, 0, n);
            pendingShorts -= n;
            return n;
        }

        /// <summary>Runs a script in the page and returns its JSON result (for the check and the HTTP API's diagnostics; null if the page is not there).</summary>
        public string? Evaluate(string script, int timeoutMs = 5000)
        {
            var f = hostForm;
            if (f == null || !f.IsHandleCreated || Volatile.Read(ref disposed) != 0) return null;
            var tcs = new TaskCompletionSource<string?>();
            try
            {
                f.BeginInvoke(async () =>
                {
                    try { tcs.TrySetResult(view?.CoreWebView2 is { } c ? await c.ExecuteScriptAsync(script) : null); }
                    catch (Exception ex) { tcs.TrySetResult(null); System.Console.WriteLine($"[WideMess Virtual] script failed: {ex.Message}"); }
                });
            }
            catch (Exception) { return null; }
            return tcs.Task.Wait(timeoutMs) ? tcs.Task.Result : null;
        }

        /// <summary>Reset: the page reloads (the game starts again at its title).</summary>
        public void Reset() => OnHost(() => view?.CoreWebView2?.Reload());

        public byte[] ExportSave() => Array.Empty<byte>();
        public void ImportSave(byte[] data) { }
        public bool TrySwapCore(CoreSlot slot, string id) => false;
        public string SwapRefusal(CoreSlot slot, string id) => NoMachineReason;
        public bool SupportsState => false;
        public string StateUnsupportedReason => NoMachineReason;

        /// <summary>Closes the page and stops the server (waits for it). Safe to call more than once.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            var f = hostForm;
            if (f != null)
            {
                try
                {
                    if (f.IsHandleCreated)
                        f.Invoke(() => { try { shared?.Dispose(); } catch { } shared = null; try { view?.Dispose(); } catch { } f.Close(); });
                }
                catch (Exception) { }
                hostThread.Join(5000);
            }
            server.Dispose();
            try { shared?.Dispose(); } catch { }
        }

        /// <summary>The window the web view lives in: real (WebView2 needs one), never shown on a screen, not in the task bar or Alt+Tab.</summary>
        private sealed class OffscreenForm : Form
        {
            public OffscreenForm(int w, int h)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
                ClientSize = new Size(w, h);
                BackColor = Color.Black;
                Text = "WideMess Virtual";
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x80 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */;
                    return cp;
                }
            }
        }
    }
}
