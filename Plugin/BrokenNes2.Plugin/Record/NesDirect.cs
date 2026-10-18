// Round 8 (record-nes): how the plugin turns FL's note into the NES chip's registers, in ONE place. Direct mode (Bn2Plugin.Apply) and a NES preset recorded from a game (NesPresetPlayer in its
// track mode) both call these functions, and the NES transcription searches the pitch bends and controller-7 values with them, so a recorded note's period and volume are found with the very
// arithmetic that will play them (the same rule as the SNES transcription's SnesSampler.PitchForTuned). Changing anything here changes what every Direct-mode note writes: the NES `direct`
// check holds it to the expressions it replaced (Bn2Plugin.Apply before round 8).
using NesEmulator.Plugin;

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

    private static void W(NesApuInstrument c, int reg, int value)
    {
        if (c.Shadow(reg) != (byte)value) c.Write(reg, (byte)value);
    }

    /// <summary>A sounding note's registers on channel <paramref name="ch"/> (0/1 pulse, 2 triangle, 3 noise): Direct mode's control-rate step, moved here unchanged from Bn2Plugin.Apply. <paramref name="level"/>
    /// comes back 0 when a pulse's period is below 8 (mute). Returns the period now (the caller passes it back as <paramref name="curPeriod"/>: a high-bit change rewrites the fourth register).</summary>
    public static int Write(NesApuInstrument c, int ch, int duty, int noiseMode, ref int level, double cents, bool retrigger, int curPeriod)
    {
        switch (ch)
        {
            case 0:
            case 1:
            {
                int b = ch * 4;
                int period = PulsePeriod(cents);
                if (period < 8) level = 0;                       // the pulse channel mutes below period 8
                period = Math.Max(period, 8);
                W(c, b, (duty << 6) | 0x30 | level);             // length halt + constant volume
                W(c, b + 1, 0x08);                               // sweep off
                int hi = (period >> 8) & 7, lo = period & 0xFF;
                if (retrigger || hi != ((curPeriod >> 8) & 7))
                {
                    W(c, b + 2, lo);
                    c.Write(b + 3, (byte)(hi | 0xF8));           // a $4003 write restarts the waveform: only on a new note or a high-bit change
                }
                else W(c, b + 2, lo);
                return period;
            }
            case 2:
            {
                int period = TrianglePeriod(cents);
                W(c, 8, level > 0 ? 0xFF : 0x80);                // the triangle has no volume: on or off
                int hi = (period >> 8) & 7, lo = period & 0xFF;
                if (retrigger || hi != ((curPeriod >> 8) & 7))
                {
                    W(c, 0x0A, lo);
                    c.Write(0x0B, (byte)(hi | 0xF8));
                }
                else W(c, 0x0A, lo);
                return period;
            }
            default:
            {
                // Noise has 16 fixed pitches: higher notes pick a shorter period. Bends step through them.
                int idx = NoiseIndex(cents);
                W(c, 0x0C, 0x30 | level);
                W(c, 0x0E, (noiseMode << 7) | idx);
                if (retrigger) c.Write(0x0F, 0xF8);
                return idx;
            }
        }
    }

    /// <summary>The channel falls silent (no note held): Direct mode's, moved here unchanged.</summary>
    public static void Silence(NesApuInstrument c, int ch, int duty)
    {
        switch (ch)
        {
            case 0: case 1: W(c, ch * 4, (duty << 6) | 0x30); break;
            case 2: W(c, 8, 0x80); break;
            default: W(c, 0x0C, 0x30); break;
        }
    }

    /// <summary>The MIDI key whose noise index is <paramref name="index"/> at no bend (the middle of its three semitones).</summary>
    public static int NoiseKey(int index) => 70 - 3 * index;

    /// <summary>The musical pitch (MIDI, fractional) of a pulse or triangle timer.</summary>
    public static double MidiOf(int period, bool triangle) => 69 + 12 * Math.Log2(CpuHz / ((triangle ? 32.0 : 16.0) * (period + 1.0)) / 440.0);
}
