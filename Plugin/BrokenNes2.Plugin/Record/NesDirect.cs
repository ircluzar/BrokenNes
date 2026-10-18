// Round 8 (record-nes): how the plugin turns FL's note into the NES chip's registers, in ONE place. Direct mode (Bn2Plugin.Apply) and a NES preset recorded from a game (NesPresetPlayer in its
// track mode) both call these functions, and the NES transcription searches the pitch bends and controller-7 values with them, so a recorded note's period and volume are found with the very
// arithmetic that will play them (the same rule as the SNES transcription's SnesSampler.PitchForTuned). Changing anything here changes what every Direct-mode note writes: the NES `direct`
// check holds it to the expressions it replaced (Bn2Plugin.Apply before round 8).
namespace BrokenNes2.Record;

public static class NesDirect
{
    public const double CpuHz = 1789773.0;

    /// <summary>FL's pitch (cents from middle C, the voice's FinalLevels.Pitch plus the instance's Coarse and Fine) as a frequency.</summary>
    public static double Freq(double cents) => 440.0 * Math.Pow(2.0, ((60.0 + cents / 100.0) - 69.0) / 12.0);

    /// <summary>The pulse timer for a pitch, capped at $7FF (the lowest note). Below 8 the channel is mute: the caller writes period 8 at volume 0.</summary>
    public static int PulsePeriod(double cents)
    {
        int period = (int)Math.Round(CpuHz / (16.0 * Freq(cents)) - 1.0);
        if (period > 0x7FF) period = 0x7FF;
        return period;
    }

    /// <summary>The triangle timer for a pitch (2 .. $7FF).</summary>
    public static int TrianglePeriod(double cents) => Math.Clamp((int)Math.Round(CpuHz / (32.0 * Freq(cents)) - 1.0), 2, 0x7FF);

    /// <summary>The noise period index for a pitch: 16 fixed pitches, a step for every three semitones (index 15 below E1, index 0 from A4 up).</summary>
    public static int NoiseIndex(double cents)
    {
        double semis = 60.0 + cents / 100.0;
        return Math.Clamp(15 - (int)Math.Floor((semis - 24.0) / 3.0), 0, 15);
    }

    /// <summary>The chip's volume (0-15) for FL's level of a voice (0..1, FL's float: <c>15 * vol</c> is a float product, as it always was) and the instance's Volume parameter (0..1).</summary>
    public static int Level(float vol, double master) => Math.Clamp((int)Math.Round(15 * vol * master), 0, 15);

    /// <summary>The MIDI key whose noise index is <paramref name="index"/> at no bend (the middle of its three semitones).</summary>
    public static int NoiseKey(int index) => 70 - 3 * index;

    /// <summary>The musical pitch (MIDI, fractional) of a pulse or triangle timer.</summary>
    public static double MidiOf(int period, bool triangle) => 69 + 12 * Math.Log2(CpuHz / ((triangle ? 32.0 : 16.0) * (period + 1.0)) / 440.0);
}
