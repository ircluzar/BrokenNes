using NesEmulator.Plugin;
using NesEmulator.Sega;

namespace NesEmulator.X86;

/// <summary>
/// The DOS PC's sound as Nano DOS plays it (Nano/dos/src/chip.js): Core's <see cref="Mame036Opl"/> (the x86 console's AdLib engine "M036": MAME 0.36's fmopl at
/// 3,579,545 Hz / 44.1 kHz, as <c>Mame036OplEngine</c> makes it) and the PC speaker as <c>x86/PcSpeaker.cs</c> draws it (port 61h on, the 8254's channel 2 in
/// mode 3: <c>Pit8254.GetOut1</c>; four points averaged per sample, level x 5000 / 4), summed and saturated as <c>PcSession.ReadSamples</c> mixes them, then
/// resampled to the host rate by <see cref="StreamResampler"/>. The speaker's lines are restated here from those two files (a PcMachine is not needed for a
/// square wave); the OPL2 is Core's own class.
/// </summary>
public sealed class NanoDosSound
{
    public const int PcRate = 44100, OplClock = 3579545;
    public const long PitFreq = 1193182;
    public readonly Mame036Opl Opl = new(OplClock, PcRate);
    private readonly StreamResampler res = new();
    private readonly float[] one = new float[1];
    public long Samples { get; private set; }
    private bool spkOn;
    public int SpkCount { get; private set; } = 0x10000;
    private long spkLoad;

    public NanoDosSound(int sampleRate) { res.SetRates(PcRate, sampleRate); }

    private static long PitOfSample(long n) => n * PitFreq / PcRate;

    public void OplWrite(int reg, byte v) { Opl.Write(0, reg); Opl.Write(1, v); }

    /// <summary>The speaker on (port 61h = 3) or off, and channel 2's count: a new count (or switching on) reloads the 8254, its phase starting now.</summary>
    public void Speaker(bool on, int count)
    {
        if (count != SpkCount || !spkOn && on) { SpkCount = count; spkLoad = PitOfSample(Samples); }
        spkOn = on;
    }

    public void Run(int n)
    {
        for (int i = 0; i < n; i++)
        {
            int spk = 0;
            if (spkOn)
            {
                long t0 = PitOfSample(Samples), t1 = PitOfSample(Samples + 1);
                int level = 0;
                for (int k = 0; k < 4; k++) { long t = Math.Max(t0 + (t1 - t0) * k / 4, spkLoad); if ((t - spkLoad) % SpkCount < ((SpkCount + 1) >> 1)) level++; }
                spk = level * 5000 / 4;
            }
            int s = spk + Opl.Update();
            s = s < -32768 ? -32768 : s > 32767 ? 32767 : s;
            one[0] = s * (1f / 32768f); res.Write(one);
            Samples++;
        }
    }

    /// <summary>Fills left[off..off+n) and right (null: mono) with the same mono samples, as chip.js's render.</summary>
    public void Render(float[] l, float[]? r, int off, int n)
    {
        while (res.Available < n) Run(1);
        res.Read(l.AsSpan(off, n));
        if (r != null) Array.Copy(l, off, r, off, n);
    }
}
