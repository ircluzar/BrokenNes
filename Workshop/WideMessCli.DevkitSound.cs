using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>The devkit's view of the WideMess sound chip's second DSP page (spec 4). Before milestone 3 there is none: the counts stay n/a.</summary>
internal static partial class WideMessCli
{
    private static bool WideSoundOn(DSP_SFC dsp) => false;
    private static int WideRead(DSP_SFC dsp, int addr) => 0;
    private static void WideWrite(DSP_SFC dsp, int addr, byte value) { }
}
