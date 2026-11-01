using System;

namespace NesEmulator.Sega;

/// <summary>
/// A Sega sound unit that can hand out each of its voices on its own, in step with its mix (round 3, track C3b: the FL plugin's per-channel stems for Master System, Game Gear and Genesis games).
/// <para>The stems come from the same run of the chips as the mix, not from a second copy of them: the unit keeps one band-limited stream per wanted voice beside its mix and delivers the same
/// number of frames of it each time <c>ReadSamples</c> delivers frames of the mix, so the stems add up to the mix (to the rounding of the mix's 16-bit samples) and a stem that is not wanted costs nothing.</para>
/// <para>Stem numbers are the unit's own (<see cref="StemName"/> says what each is): the PSG's four voices are always 0 to 3 (tone 1, 2, 3, noise); the FM chip's voices follow. Every stem is mono at the unit's
/// <c>SampleRate</c>, as floats where 1.0 is the full scale of the mix's 16-bit samples; a Game Gear voice's two sides are averaged, as the plugin does for the mix.</para>
/// </summary>
public interface IVoiceStems
{
    /// <summary>How many stems the unit has.</summary>
    int StemCount { get; }
    string StemName(int stem);
    /// <summary>Which stems are being made (a bit per stem). A stem switched on starts at the unit's present time; switching one off frees it of all work.</summary>
    long StemMask { get; set; }
    /// <summary>Moves up to <c>dest.Length</c> finished frames of a stem out; returns how many. Frames come in the order the mix delivered its own.</summary>
    int ReadStem(int stem, Span<float> dest);
}

/// <summary>Names and numbers of the stems every Sega unit shares.</summary>
public static class SegaStems
{
    public const int PsgTone1 = 0, PsgTone2 = 1, PsgTone3 = 2, PsgNoise = 3, PsgCount = 4;
    /// <summary>The Genesis unit: stems 4 to 9 are FM 1 to 6, stem 10 the DAC (the YM2612's channel 6 while the DAC is on).</summary>
    public const int MdFm1 = 4, MdDac = 10, MdCount = 11;
    /// <summary>The Master System FM unit: stems 4 to 12 are FM 1 to 9, then the five rhythm voices in the YM2413's own order (bass drum, hi-hat, snare, tom, cymbal).</summary>
    public const int SmsFm1 = 4, SmsRhythm1 = 13, SmsFmCount = 18;

    public static readonly string[] PsgNames = ["PSG tone 1", "PSG tone 2", "PSG tone 3", "PSG noise"];
    public static readonly string[] RhythmNames = ["Bass drum", "Hi-hat", "Snare", "Tom", "Cymbal"];
}

/// <summary>A queue of float frames with a fixed capacity (no allocation after construction). When a consumer does not keep up the oldest frames go and <see cref="Dropped"/> says how many.</summary>
public sealed class StemQueue
{
    private readonly float[] data;
    private readonly int mask;
    private long read, write;

    public StemQueue(int capacityPowerOfTwo = 1 << 15)
    {
        if ((capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0) throw new ArgumentException("capacity must be a power of two");
        data = new float[capacityPowerOfTwo];
        mask = capacityPowerOfTwo - 1;
    }

    public int Count => (int)(write - read);
    public long Dropped { get; private set; }

    public void Clear() { read = write; }

    public void Push(float v)
    {
        if (write - read > mask) { read = write - mask; Dropped++; }
        data[write & mask] = v;
        write++;
    }

    public void Push(ReadOnlySpan<float> frames)
    {
        for (int i = 0; i < frames.Length; i++) Push(frames[i]);
    }

    public int Pop(Span<float> dest)
    {
        int n = (int)Math.Min(dest.Length, write - read);
        for (int i = 0; i < n; i++) dest[i] = data[(read + i) & mask];
        read += n;
        return n;
    }
}

/// <summary>A ring addressed by an absolute frame number, for a stem that has to line up with a queue that is indexed the same way (the Master System FM unit's PSG and FM queues, which are paired by index).</summary>
public sealed class IndexedRing
{
    private readonly float[] data;
    private readonly int mask;

    public IndexedRing(int capacityPowerOfTwo)
    {
        data = new float[capacityPowerOfTwo];
        mask = capacityPowerOfTwo - 1;
    }

    public float this[long index] { get => data[index & mask]; set => data[index & mask] = value; }
}
