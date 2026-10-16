using System;

namespace NesEmulator.Sega;

/// <summary>
/// What a Sega sound bridge hands its session: the mono samples of the NES-family sound core that plays the game's sound, as stereo 16-bit pairs at the machine's sample rate. Most chips run at the machine's 44.1 kHz and pass straight through
/// (exactly as before); a chip with a rate of its own (the SNES's sound unit at 32 kHz, the Game Boy's at 48 kHz) is converted by a <see cref="ChipRateConverter"/>, so the unit delivers the same amount of sound for the same frames as the console's
/// own unit and says so in <see cref="SampleRate"/>.
/// </summary>
public sealed class NesChipOutput
{
    private readonly IAPU back;
    private readonly ChipRateConverter? resampler;
    private readonly float[] pull = new float[2048], converted = new float[2048];

    /// <summary>The rate the unit delivers: always the machine's.</summary>
    public int SampleRate { get; }
    /// <summary>The rate the NES-family core runs at; different from <see cref="SampleRate"/> when the output is converted.</summary>
    public int ChipRate { get; }

    public NesChipOutput(IAPU back, int machineRate)
    {
        this.back = back;
        ChipRate = back.GetSampleRate();
        if (ChipRate != machineRate) resampler = new ChipRateConverter(ChipRate, machineRate);
        SampleRate = machineRate;
    }

    /// <summary>True when the chip's own rate differs from the machine's and its sound is converted.</summary>
    public bool Converted => resampler != null;

    /// <summary>The conversion starts again from silence (the unit was reset).</summary>
    public void Reset() => resampler?.Reset();

    /// <summary>Fills <paramref name="dest"/> with left-right pairs of what the chip has made; returns the number of shorts written.</summary>
    public int ReadStereo(Span<short> dest)
    {
        int written = 0;
        while (written + 1 < dest.Length)
        {
            int want = Math.Min(pull.Length, (dest.Length - written) / 2);
            int got;
            if (resampler == null) got = back.ReadSamples(pull.AsSpan(0, want));
            else
            {
                got = resampler.Pull(converted.AsSpan(0, want));
                if (got <= 0)
                {
                    int n = back.ReadSamples(pull);
                    if (n <= 0) break;
                    resampler.Push(pull.AsSpan(0, n));
                    continue;
                }
            }
            if (got <= 0) break;
            var src = resampler == null ? pull : converted;
            for (int i = 0; i < got; i++)
            {
                short s = (short)Math.Clamp(src[i] * 32767f, -32768f, 32767f);
                dest[written++] = s; dest[written++] = s;
            }
        }
        return written;
    }
}
