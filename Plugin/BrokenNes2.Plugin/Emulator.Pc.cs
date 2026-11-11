// The x86 PC console (console 5): the emulator's side. Direct mode only: every instance plays a PcVoice of its own (Bn2Plugin.Pc.cs); ROM mode is refused (SetMode keeps 0, a project saying ROM mode on a PC chip opens in Direct mode).
namespace BrokenNes2;

public sealed partial class Emulator
{
    /// <summary>Direct mode on a PC chip: every instance plays one voice of its own x86 sound device.</summary>
    public bool PcDirectMode => Mode == 0 && IsPcChip(Chip);

    /// <summary>Whether a chip index is a PC console chip (read on the audio thread: a field test, no allocation).</summary>
    public static bool IsPcChip(int chip) => (uint)chip < (uint)Cx.Chips.Count && Cx.Chips[chip].IsPc;
}
