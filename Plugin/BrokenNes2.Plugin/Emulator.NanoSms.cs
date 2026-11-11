// The Nano SMS chip (NANOSMS, Master System console): the emulator's side. Direct mode only: every instance plays a NanoSmsEngine of its own (Bn2Plugin.NanoSms.cs).
// ROM mode on the same chip runs a Master System game on the Master System's own sound unit.
namespace BrokenNes2;

public sealed partial class Emulator
{
    /// <summary>Direct mode on the Nano SMS chip: every instance plays the web library's engine (Nano/sms: the SN76489, 24 presets).</summary>
    public bool NanoSmsDirectMode => Mode == 0 && IsNanoSmsChip(Chip);

    /// <summary>Whether a chip index is the Nano SMS chip (read on the audio thread: a field test, no allocation).</summary>
    public static bool IsNanoSmsChip(int chip) => (uint)chip < (uint)Cx.Chips.Count && Cx.Chips[chip].IsNanoSms;
}
