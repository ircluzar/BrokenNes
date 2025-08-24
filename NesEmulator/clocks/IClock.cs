using System.Threading;
using System.Threading.Tasks;

namespace NesEmulator
{
    // Contract for pluggable Clock Cores controlling the main emulation cadence
    public interface IClock
    {
        // Stable identifier (suffix) used for discovery and UI selection, e.g., "FMC", "CLR"
        string CoreId { get; }
        // Human-friendly name for UI
        string DisplayName { get; }
        // Brief description for tooltips/settings
        string Description { get; }

    // Relative performance hint vs. baseline (percent). 0 = baseline
    int Performance { get; }
    // User-facing quality/fit score, 0-5
    int Rating { get; }

        // Start the clock loop with the provided host. Implementations should return quickly
        // and perform their loop on a background task respecting the provided CancellationToken.
        ValueTask StartAsync(IClockHost host, CancellationToken ct);

        // Stop the clock loop promptly; should be idempotent.
        void Stop();

        // Optional hint from environment (e.g., tab visibility change) to throttle/suspend.
        void OnVisibilityChanged(bool visible);
    }

    // Host surface exposed to Clock Cores – intentionally minimal for Phase 1.
    // Will be extended in Phase 2 when CLR loop implementation lands.
    public interface IClockHost
    {
        // Whether emulation should currently be running
        bool IsRunning { get; }

        // Request JS-driven RAF loop start/stop (FMC path). Safe to call multiple times.
        ValueTask RequestStartJsLoopAsync();
        ValueTask RequestStopJsLoopAsync();

        // Execute one emulation frame and return immediately (host-defined cadence control)
        // Implementations can poll this to produce frames when not using JS RAF.
    void RunFrame();

    // Build the per-frame payload (framebuffer + audio) from host state.
    // This mirrors what FrameTick returns to JS in FMC mode.
    BrokenNes.Emulator.FramePayload RunFrameAndBuildPayload();

    // Present a payload using the host's presentation path (JS interop).
    ValueTask PresentAsync(BrokenNes.Emulator.FramePayload payload);
    }
}
