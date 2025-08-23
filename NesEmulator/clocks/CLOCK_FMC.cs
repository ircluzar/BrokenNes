using System.Threading;
using System.Threading.Tasks;

namespace NesEmulator
{
    // Default clock core: delegates frame cadence to existing JS requestAnimationFrame loop.
    // This preserves current behavior and performance characteristics.
    public sealed class CLOCK_FMC : IClock
    {
        public string CoreId => "FMC";
        public string DisplayName => "FMC (JS driver)";
        public string Description => "JavaScript requestAnimationFrame drives FrameTick(); JS presents frame & audio.";

        public async ValueTask StartAsync(IClockHost host, CancellationToken ct)
        {
            // Idempotent: ask host to start JS loop; host ensures single active loop
            await host.RequestStartJsLoopAsync();
        }

        public void Stop()
        {
            // JS loop stop is requested via host; no persistent state kept here
        }

        public void OnVisibilityChanged(bool visible)
        {
            // JS rAF is already visibility-friendly; nothing to do.
        }
    }
}
