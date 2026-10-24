using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>The devkit's view of the WideMess sound chip's second DSP page (spec 4): live only while the sound unit is unlocked.</summary>
internal static partial class WideMessCli
{
    private static bool WideSoundOn(DSP_SFC dsp) => dsp.WideOn;
    private static int WideRead(DSP_SFC dsp, int addr) => dsp.ReadHigh(addr);
    private static void WideWrite(DSP_SFC dsp, int addr, byte value) => dsp.WriteHigh(addr, value);
}
