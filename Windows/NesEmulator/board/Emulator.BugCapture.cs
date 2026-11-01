using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using NesEmulator.Debugging.Capture;
using NesEmulator.Systems;

namespace BrokenNes
{
    /// <summary>
    /// BrokenNes Lite's bug capture (BrokenDev milestone D6, docs/projects/brokendev-debugging-plan.md): the same rolling buffer as the desktop
    /// app (<see cref="BugCaptureRing"/>, shared), fed from the page's frame call; the Bug button saves the last 30 s as Lite's download (a .zip of
    /// the bundle's files: the movie, the ROM, the sound, the pictures, bug.json). The page has no BrokenDev workspace to write to: the zip goes
    /// to the browser's downloads, and <c>brokendev.ps1 bug &lt;zip&gt;</c> replays it. Off (<see cref="SetBugCapture"/>), nothing of it runs.
    /// </summary>
    public partial class Emulator
    {
        private readonly BugCaptureRing bugRing = new();
        private object? bugAttachedTo;
        private int bugAttachedLoads = -1;
        private string? bugPendingBreak;
        private bool bugPowerOn = true;

        /// <summary>Keep the last 30 s (on by default, as the desktop app).</summary>
        public bool BugCaptureEnabled { get; private set; } = true;

        /// <summary>Something outside the game changed it: the history starts again before the next frame.</summary>
        private void BugBreak(string why) { if (BugCaptureEnabled) Interlocked.Exchange(ref bugPendingBreak, why); }

        private bool BugPrepare(object machine, int loads, Func<BugCaptureSource> source, int sampleRate, double fps)
        {
            if (!BugCaptureEnabled)
            {
                if (bugRing.Attached) { bugRing.Detach(); bugAttachedTo = null; }
                return false;
            }
            if (!ReferenceEquals(bugAttachedTo, machine) || loads != bugAttachedLoads)
            {
                bugRing.Attach(source(), sampleRate, fps, bugPowerOn);
                bugAttachedTo = machine; bugAttachedLoads = loads;
                Interlocked.Exchange(ref bugPendingBreak, null);
            }
            bugPowerOn = false;
            if (Interlocked.Exchange(ref bugPendingBreak, null) is { } why) bugRing.Break(why);
            return true;
        }

        /// <summary>Before each NES frame (the page's frame call): the pads it gets.</summary>
        private void BugBeforeNesFrame()
        {
            var n = nes;
            if (n == null || (!BugCaptureEnabled && !bugRing.Attached)) return;
            if (string.Equals(n.RomName, "test.nes", StringComparison.OrdinalIgnoreCase)) { if (bugRing.Attached) { bugRing.Detach(); bugAttachedTo = null; } return; }
            if (!ReferenceEquals(bugAttachedTo, n) || n.RomLoads != bugAttachedLoads) { bugPowerOn = true; n.StateLoaded = () => BugBreak("a state was loaded"); }
            if (!BugPrepare(n, n.RomLoads, () => BugCaptureSource.For(n), n.GetAudioSampleRate(), 60.0)) return;
            bugRing.Frame(inputState, inputStateP2);
        }

        private void BugBeforeSessionFrame(IConsoleSession s, PadButtons p1, PadButtons p2)
        {
            if (!BugCaptureEnabled && !bugRing.Attached) return;
            var c = s.Console;
            if (!ReferenceEquals(bugAttachedTo, s)) bugPowerOn = sessionFrames == 0;
            if (!BugPrepare(s, 0, () => BugCaptureSource.For(s, () => $"{ConsoleCore(c, CoreSlot.Cpu)}/{ConsoleCore(c, CoreSlot.Ppu)}/{ConsoleCore(c, CoreSlot.Apu)}"), s.SampleRate, s.FramesPerSecond)) return;
            bugRing.Frame(p1, p2);
        }

        private void BugAudio(float[]? samples, int count) { if (samples != null && count > 0 && bugRing.Attached) bugRing.Audio(samples, count); }

        /// <summary>On or off (the page's switch; the tests). Off detaches the ring at the next frame: nothing is recorded.</summary>
        [JSInvokable]
        public string SetBugCapture(bool on) { BugCaptureEnabled = on; return BugStatusJson(); }

        /// <summary>The capture's state as JSON (for the page and the tests).</summary>
        [JSInvokable]
        public string BugStatusJson() => System.Text.Json.JsonSerializer.Serialize(new BugStatusView
        {
            Enabled = BugCaptureEnabled, Recording = bugRing.Attached, Console = bugRing.ConsoleKey, HeldSeconds = Math.Round(bugRing.HeldSeconds, 2), Frames = bugRing.FrameCount,
            BreakReason = bugRing.BreakReason, StateError = bugRing.StateError, LastStateMs = Math.Round(bugRing.LastStateMs, 3), MaxStateMs = Math.Round(bugRing.MaxStateMs, 3), LastStateBytes = bugRing.LastStateBytes,
        }, BugStatusJsonContext.Default.BugStatusView);

        /// <summary>
        /// The capture as Lite's download: the bundle's files as one zip (its name, its bytes), or why there is none. Runs between frames: the
        /// page's frame calls and this one are on the same (only) thread.
        /// </summary>
        [JSInvokable]
        public BugZip CaptureBugZip(string note)
        {
            if (!BugCaptureEnabled) return new BugZip { Error = "Bug capture is off: nothing was recorded." };
            if (!bugRing.Attached) return new BugZip { Error = "No game is being recorded (the idle screen, or no game loaded)." };
            try
            {
                if (Interlocked.Exchange(ref bugPendingBreak, null) is { } why) bugRing.Break(why);
                var d = bugRing.Capture();
                var meta = new BugMeta { Note = (note ?? "").Replace("\r", " ").Replace("\n", " ").Trim(), App = "BrokenNes Lite", CapturedUtc = DateTime.UtcNow, CapturedLocal = DateTime.Now };
                if (session is { } s)
                {
                    meta.RomName = sessionRomName; meta.Rom = sessionRom ?? Array.Empty<byte>();
                    meta.Cores = $"{ConsoleCore(s.Console, CoreSlot.Cpu)}/{ConsoleCore(s.Console, CoreSlot.Ppu)}/{ConsoleCore(s.Console, CoreSlot.Apu)}";
                    meta.Settings.Add(("description", s.Description));
                }
                else if (nes is { } n)
                {
                    meta.RomName = n.RomName; meta.Rom = n.RomFile ?? Array.Empty<byte>();
                    meta.Cores = BugCaptureSource.NesCores(n); meta.NtscTiming = BugCaptureSource.NesNtsc(n);
                }
                var files = BugBundle.Files(d, meta);
                return new BugZip { Name = BugBundle.ZipName(meta.RomName, meta.CapturedLocal), Data = BugBundle.Zip(files), Seconds = Math.Round(d.Seconds, 2), Frames = d.Frames, NoMovie = d.NoMovieWhy };
            }
            catch (InvalidOperationException e) { return new BugZip { Error = "Nothing to capture: " + e.Message }; }
        }

        /// <summary>The Bug button: the note asked (the browser's own prompt; Cancel discards), then the zip handed to the browser as a download.</summary>
        public async Task CaptureBugDownloadAsync()
        {
            bool wasRunning = nesController.IsRunning;
            if (wasRunning) await PauseEmulation();
            try
            {
                string? note = await JS.InvokeAsync<string?>("nesInterop.askBugNote", $"Capture the last {Math.Min(BugCaptureRing.Seconds, bugRing.HeldSeconds):F0} s. A one-line note (optional):");
                if (note == null) { Status.Set("Bug capture discarded"); return; }
                var z = CaptureBugZip(note);
                if (z.Error != null) { Status.Set(z.Error); return; }
                await JS.InvokeVoidAsync("nesInterop.downloadBytes", z.Name, z.Data, "application/zip");
                Status.Set($"Bug saved: {z.Name} ({z.Seconds:F0} s{(z.NoMovie != null ? ", no movie: " + z.NoMovie : "")})");
            }
            finally
            {
                if (wasRunning) await StartEmulation();
                StateHasChanged();
            }
        }

        public sealed class BugZip
        {
            public string? Name { get; set; }
            public byte[]? Data { get; set; }
            public double Seconds { get; set; }
            public long Frames { get; set; }
            public string? NoMovie { get; set; }
            public string? Error { get; set; }
        }

        public sealed class BugStatusView
        {
            public bool Enabled { get; set; }
            public bool Recording { get; set; }
            public string Console { get; set; } = "";
            public double HeldSeconds { get; set; }
            public long Frames { get; set; }
            public string BreakReason { get; set; } = "";
            public string StateError { get; set; } = "";
            public double LastStateMs { get; set; }
            public double MaxStateMs { get; set; }
            public int LastStateBytes { get; set; }
        }
    }

    [System.Text.Json.Serialization.JsonSerializable(typeof(Emulator.BugStatusView))]
    internal partial class BugStatusJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
}
