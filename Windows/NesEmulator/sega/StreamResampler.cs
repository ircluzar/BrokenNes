using System;

namespace NesEmulator.Sega;

/// <summary>
/// Converts a mono stream from one sample rate to another, a block at a time, without allocating while it runs: a windowed-sinc interpolator (Blackman window, 32 taps each side of the sample, the kernel tabulated at
/// 512 positions between two input samples, each position normalised to unit gain) whose cutoff is 95 percent of the lower of the two Nyquist rates, so a 48 kHz chip played at 44.1 kHz loses what 44.1 kHz cannot hold instead of folding it back
/// (measured on the kernel: a 14 kHz tone of a 32 kHz chip passes at -0.02 dB with its image at 18 kHz under -90 dB; a 20 kHz tone of a 48 kHz chip passes at -0.8 dB and a 23 kHz one, which would fold to 21.1 kHz, is -73 dB).
/// </summary>
/// <remarks>
/// Used by the Sega sound bridges (a Sega game's sound played by a NES chip): a NES-family sound core runs at its own rate (the SNES's sound unit at 32 kHz, the Game Boy's at 48 kHz) and the Sega session promises the machine's
/// rate (44.1 kHz), so the bridge converts what the chip makes. Without that a session either lies about its rate (the Genesis one: the tone came out at 606 Hz for 440 and at 404 for 440) or hands the host a rate that differs
/// from the console's own unit (the Master System's), which a host that fixes its audio device on the first rate it sees plays at the wrong speed after a swap.
/// <para>The stream has no latency in samples: output sample k is the signal at input time k x (source rate / target rate) minus the kernel's half width, so N input samples give exactly the output samples that fit in N input
/// samples' time (the first outputs come from silence before the stream's start), and the sound is delayed by 32 input samples, 0.7 to 1 millisecond.</para>
/// </remarks>
public sealed class StreamResampler
{
    private const int Half = 32, Width = 2 * Half, Phases = 512;

    private readonly float[] kernel;           // (Phases + 1) rows of Width weights
    private readonly double step;               // input samples per output sample
    private float[] input = new float[1 << 14];
    private int count;                          // samples held in input
    private long baseIndex;                     // the stream index of input[0]
    private long outIndex;                      // the next output sample's number

    public int SourceRate { get; }
    public int TargetRate { get; }

    public StreamResampler(int sourceRate, int targetRate)
    {
        if (sourceRate <= 0 || targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        SourceRate = sourceRate; TargetRate = targetRate;
        step = (double)sourceRate / targetRate;
        double cutoff = 0.95 * Math.Min(1.0, (double)targetRate / sourceRate);       // of the source's Nyquist rate
        kernel = new float[(Phases + 1) * Width];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases, total = 0;
            var row = new double[Width];
            for (int t = 0; t < Width; t++)
            {
                double d = (t - Half + 1) - frac;                       // distance of input sample (i0 - Half + 1 + t) from the point
                double sinc = Math.Abs(d) < 1e-12 ? 1.0 : Math.Sin(Math.PI * cutoff * d) / (Math.PI * cutoff * d);
                double w = Math.Abs(d) <= Half ? 0.42 + 0.5 * Math.Cos(Math.PI * d / Half) + 0.08 * Math.Cos(2 * Math.PI * d / Half) : 0;
                row[t] = sinc * w; total += row[t];
            }
            for (int t = 0; t < Width; t++) kernel[p * Width + t] = (float)(row[t] / total);
        }
        Reset();
    }

    /// <summary>Back to the start of a stream: nothing pending, the history silent.</summary>
    public void Reset()
    {
        Array.Clear(input);
        count = Width; baseIndex = -Width;      // silence before the stream's first sample
        outIndex = 0;
    }

    /// <summary>Adds source samples to the stream.</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        if (count + samples.Length > input.Length)
        {
            Compact();
            if (count + samples.Length > input.Length) Array.Resize(ref input, Math.Max(input.Length * 2, count + samples.Length));
        }
        samples.CopyTo(input.AsSpan(count));
        count += samples.Length;
    }

    /// <summary>Takes as many output samples as the source samples pushed so far allow (up to the length of <paramref name="dest"/>); returns how many.</summary>
    public int Pull(Span<float> dest)
    {
        int n = 0;
        long lastIndex = baseIndex + count - 1;                          // the newest source sample we have
        while (n < dest.Length)
        {
            double t = outIndex * step - Half;
            long i0 = (long)Math.Floor(t);
            if (i0 + Half > lastIndex) break;                             // the kernel's right edge is not here yet
            double frac = t - i0;
            int row = (int)Math.Round(frac * Phases);
            int at = (int)(i0 - Half + 1 - baseIndex);       // never negative: Compact keeps the history the next output reads
            float acc = 0;
            int k = row * Width;
            for (int j = 0; j < Width; j++) acc += input[at + j] * kernel[k + j];
            dest[n++] = acc;
            outIndex++;
        }
        Compact();
        return n;
    }

    /// <summary>Drops the samples no later output can use (all but the kernel's history).</summary>
    private void Compact()
    {
        long nextI0 = (long)Math.Floor(outIndex * step - Half);
        long keepFrom = nextI0 - Half + 1;                                // the oldest sample the next output reads
        int drop = (int)Math.Clamp(keepFrom - baseIndex, 0, count);
        if (drop <= 0) return;
        Array.Copy(input, drop, input, 0, count - drop);
        count -= drop; baseIndex += drop;
    }
}
