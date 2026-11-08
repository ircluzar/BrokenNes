// The Nano GB chip (chip 30, NANOGB, Game Boy console): the emulator's side. Direct mode only: every instance plays a NanoGbEngine of its own (Bn2Plugin.NanoGb.cs).
// ROM mode on the same chip runs a Game Boy game on the Game Boy's own sound unit (Cx.RomApu answers "GB").
namespace BrokenNes2;

public sealed partial class Emulator
{
    /// <summary>Direct mode on the Nano GB chip: every instance plays the web library's engine (27 presets, gnuboy's sound).</summary>
    public bool NanoGbDirectMode => Mode == 0 && IsNanoGbChip(Chip);

    /// <summary>Whether a chip index is the Nano GB chip (read on the audio thread: a field test, no allocation).</summary>
    public static bool IsNanoGbChip(int chip) => (uint)chip < (uint)Cx.Chips.Count && Cx.Chips[chip].IsNanoGb;
}
