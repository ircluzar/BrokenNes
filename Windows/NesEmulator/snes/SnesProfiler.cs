using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NesEmulator.Snes;

/// <summary>
/// Opt-in per-component timers for the SFC cores (Workshop --snesbench --breakdown). When disabled
/// the cost is one static bool test per instrumented call, which is negligible even under WASM.
/// Times are nested: APU includes DSP; the CPU/bus share is what remains of the frame total.
/// </summary>
public static class SnesProfiler
{
    public static bool Enabled;
    public static long PpuTicks, ApuTicks, DspTicks;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void Reset() { PpuTicks = ApuTicks = DspTicks = 0; }
}
