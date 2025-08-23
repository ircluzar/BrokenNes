using System;
using System.Threading;
using System.Threading.Tasks;

namespace NesEmulator
{
    // Managed C# loop clock core: drives frames on a background Task, presenting via host
    public sealed class CLOCK_CLR : IClock
    {
        public string CoreId => "CLR";
        public string DisplayName => "CLR (C# Loop)";
    public string Description => "Managed loop in C# with low-overhead scheduling; C# owns cadence incl. audio timing.";

        private volatile bool _running;
        private Task? _loopTask;
        private IClockHost? _host;
        private CancellationToken _ct;
        private volatile bool _visible = true;

        public ValueTask StartAsync(IClockHost host, CancellationToken ct)
        {
            _host = host; _ct = ct; _running = true;
            if (_loopTask == null || _loopTask.IsCompleted)
            {
                _loopTask = Task.Run(LoopAsync);
            }
            return ValueTask.CompletedTask;
        }

        public void Stop()
        {
            _running = false;
        }

        public void OnVisibilityChanged(bool visible)
        {
            _visible = visible;
        }

        private async Task LoopAsync()
        {
            // Stable 60 Hz cadence with browser-safe delays and minimal yielding
            const double targetFps = 60.0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long freq = System.Diagnostics.Stopwatch.Frequency;
            double ticksPerMs = freq / 1000.0;
            long targetTicks = (long)(1000.0 / targetFps * ticksPerMs);
            long nextDue = sw.ElapsedTicks + targetTicks; // first frame due time

            // Catch-up burst limiter to avoid runaway when tab throttled
            const int maxCatchUpFrames = 2;
            int frameCtr = 0;

            while (_running && _host != null && !_ct.IsCancellationRequested)
            {
                if (!_host.IsRunning)
                {
                    // Reset schedule while paused to avoid burst on resume
                    nextDue = sw.ElapsedTicks + targetTicks;
                    try { await Task.Delay(8, _ct).ConfigureAwait(false); } catch { }
                    continue;
                }

                // Produce one frame and present (audio/video bound to this CLR cadence)
                var payload = _host.RunFrameAndBuildPayload();
                if ((payload.Framebuffer != null && payload.Framebuffer.Length > 0) || (payload.Audio != null && payload.Audio.Length > 0))
                {
                    try { await _host.PresentAsync(payload); } catch { }
                }

                // Advance schedule for next frame
                nextDue += targetTicks;

                // Compute remaining time until next due
                long now = sw.ElapsedTicks;
                long remainTicks = nextDue - now;

                // If not visible, relax cadence by one frame to reduce CPU
                if (!_visible) { remainTicks += targetTicks; }

                if (remainTicks > 0)
                {
                    // Wait until the scheduled due time to enforce 60 Hz. Use short delays in a loop to avoid long sleeps.
                    while (remainTicks > 0 && !_ct.IsCancellationRequested && _running && _host.IsRunning)
                    {
                        double ms = remainTicks / ticksPerMs;
                        if (ms < 1.0)
                        {
                            await Task.Yield();
                        }
                        else
                        {
                            int delayMs = (int)Math.Min(16, Math.Max(1, ms - 0.25));
                            try { await Task.Delay(delayMs, _ct).ConfigureAwait(false); } catch { }
                        }
                        now = sw.ElapsedTicks;
                        remainTicks = nextDue - now;
                    }
                }
                else
                {
                    // Behind schedule: drop up to N frame slots to realign without sleeping
                    int catchUp = 0;
                    while (catchUp < maxCatchUpFrames && (sw.ElapsedTicks - nextDue) > targetTicks)
                    {
                        nextDue += targetTicks;
                        catchUp++;
                    }
                    // Rare cooperative yield to keep UI responsive without impacting cadence
                    if ((++frameCtr & 15) == 0)
                    {
                        await Task.Delay(0);
                    }
                }
            }
        }
    }
}
