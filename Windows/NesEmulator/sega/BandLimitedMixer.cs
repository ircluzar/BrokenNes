using System;

namespace NesEmulator.Sega;

/// <summary>
/// Turns a chip's output, which only ever changes in steps at the chip's own clock (a square wave from a PSG, say), into audio samples at the host's rate
/// without the aliasing a plain "sample the current level" produces. Each step is added to a buffer as a band-limited impulse (a windowed-sinc kernel, positioned to a
/// thirty-second of an output sample) and the buffer is integrated on the way out, so a hard edge becomes the band-limited edge it should be. A first-order high-pass
/// (a leaky integrator, about 5 Hz) removes the DC offset of a unipolar chip.
/// </summary>
/// <remarks>
/// The technique (band-limited step synthesis) is standard signal processing; the kernel and everything around it are our own (a Blackman-windowed sinc with its
/// cutoff at 94% of Nyquist, normalised to unit area for every phase). No code from an existing synthesis library was used.
/// Latency is <see cref="Taps"/>/2 samples. Not thread safe; one instance per output channel.
/// </remarks>
public sealed class BandLimitedMixer
{
    public const int Taps = 16;
    private const int Phases = 32;
    private const int Mask = 4095;   // ring of 4096 samples: far more than a frame needs

    private static readonly float[,] Kernel = BuildKernel();
    private readonly float[] ring = new float[Mask + 1];
    private readonly double samplesPerClock;
    private readonly int sampleRate;
    private readonly float leak;
    private float sum;
    private long consumed;        // output samples handed out so far
    private double now;           // the chip time reached, in output samples

    /// <param name="clockHz">The rate the chip's output can change at (for the PSG, the CPU clock divided by 16).</param>
    /// <param name="sampleRate">The host rate.</param>
    public BandLimitedMixer(double clockHz, int sampleRate)
    {
        this.sampleRate = sampleRate;
        samplesPerClock = sampleRate / clockHz;
        leak = (float)(2 * Math.PI * 5.0 / sampleRate);
    }

    /// <summary>The output rate.</summary>
    public int SampleRate => sampleRate;

    private static float[,] BuildKernel()
    {
        const double cutoff = 0.94;
        var k = new float[Phases, Taps];
        for (int p = 0; p < Phases; p++)
        {
            double frac = (double)p / Phases;
            double total = 0;
            var tmp = new double[Taps];
            for (int t = 0; t < Taps; t++)
            {
                // sample n = floor(pos) - Taps/2 + 1 + t sits at distance (n - pos) from the impulse: (t - Taps/2 + 1) - frac
                double x = t - Taps / 2 + 1 - frac;
                double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                double w = Math.Abs(x) <= Taps / 2
                    ? 0.42 + 0.5 * Math.Cos(2 * Math.PI * x / Taps) + 0.08 * Math.Cos(4 * Math.PI * x / Taps)
                    : 0;
                tmp[t] = sinc * w;
                total += tmp[t];
            }
            for (int t = 0; t < Taps; t++) k[p, t] = (float)(tmp[t] / total);
        }
        return k;
    }

    /// <summary>The output changes by <paramref name="delta"/> at chip time <paramref name="clock"/> (in chip clocks since the start; never earlier than the previous call).</summary>
    public void Step(long clock, float delta)
    {
        if (delta == 0) return;
        double pos = clock * samplesPerClock;
        long whole = (long)Math.Floor(pos);
        int phase = (int)((pos - whole) * Phases);
        if (phase >= Phases) phase = Phases - 1;
        long first = whole - Taps / 2 + 1;
        for (int t = 0; t < Taps; t++) ring[(first + t) & Mask] += delta * Kernel[phase, t];
    }

    /// <summary>Tells the mixer the chip has run up to <paramref name="clock"/>: the samples before that (less the kernel's latency) are final.</summary>
    public void Advance(long clock) => now = clock * samplesPerClock;

    /// <summary>How many finished samples are waiting.</summary>
    public int Available => (int)Math.Max(0, (long)Math.Floor(now) - Taps / 2 - consumed);

    /// <summary>Takes up to <paramref name="count"/> finished samples (as floats, 1.0 = one full-scale channel step), integrated and high-passed.</summary>
    public int Read(Span<float> destination)
    {
        int n = Math.Min(Available, destination.Length);
        for (int i = 0; i < n; i++)
        {
            int at = (int)((consumed + i) & Mask);
            sum = sum * (1 - leak) + ring[at];
            ring[at] = 0;
            destination[i] = sum;
        }
        consumed += n;
        return n;
    }

    /// <summary>Forgets what is buffered and the DC state; chip time keeps counting (the chip's clock is monotonic across its own reset).</summary>
    public void Reset() { Array.Clear(ring); sum = 0; consumed = Math.Max(0, (long)Math.Floor(now) - Taps / 2); }

    /// <summary>Takes the time of another mixer that runs beside this one (same clock and rate): the same next output sample, nothing buffered, no DC state. For a stem that starts while the mixer it accompanies is already running
    /// (the voice's level is then handed in by the caller as its last level, so the first step is the change from there; the high-pass makes any DC in it die away within about 0.1 s).</summary>
    public void CopyTimeFrom(BandLimitedMixer other) { Array.Clear(ring); sum = 0; consumed = other.consumed; now = other.now; }

    /// <summary>The whole state: the DC integrator, the time, and the buffered samples from the first one not yet handed out to the last the kernel can still touch (a restored mixer carries on bit for bit).</summary>
    public void SaveState(System.IO.BinaryWriter w)
    {
        w.Write(sum); w.Write(consumed); w.Write(now);
        int count = (int)Math.Clamp((long)Math.Floor(now) + Taps - consumed + 1, 0, Mask + 1);
        w.Write(count);
        for (int i = 0; i < count; i++) w.Write(ring[(int)((consumed + i) & Mask)]);
    }

    public void LoadState(System.IO.BinaryReader r)
    {
        Array.Clear(ring);
        sum = r.ReadSingle(); consumed = r.ReadInt64(); now = r.ReadDouble();
        int count = r.ReadInt32();
        if (count < 0 || count > Mask + 1) throw new System.IO.InvalidDataException("band-limited mixer state: bad buffer length");
        for (int i = 0; i < count; i++) ring[(int)((consumed + i) & Mask)] = r.ReadSingle();
    }
}
