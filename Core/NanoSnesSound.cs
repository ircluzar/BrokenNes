using NesEmulator.Plugin;

namespace NesEmulator.Snes;

/// <summary>
/// The S-DSP as Nano SNES plays it (Nano/snes/src/chip.js): Core's <see cref="DSP_SFC"/> on 64 KB of sound RAM, no SPC700 (the instrument engine,
/// <see cref="NanoSnesEngine"/>, writes the registers and the sound RAM itself), its 32 kHz output resampled to the host rate by two
/// <see cref="StreamResampler"/>s (as the BrokenNes2 plugin's WmsSampler plays its DSP). The DSP runs just far enough for the samples asked for.
/// </summary>
public sealed class NanoSnesSound
{
    public const int DspRate = 32000;
    public readonly byte[] Aram = new byte[65536];
    public readonly DSP_SFC Dsp;
    private readonly StreamResampler left = new(), right = new();
    private readonly float[] one = new float[1];
    private float[] scratch = new float[512];

    /// <summary>DSP samples run so far.</summary>
    public long Samples { get; private set; }
    /// <summary>The DSP's KON / KOFF polls so far (it reads them on every other sample, the first included).</summary>
    public long Polls => (Samples + 1) >> 1;

    public NanoSnesSound(int sampleRate)
    {
        Dsp = new DSP_SFC(Aram) { Output = Push };
        left.SetRates(DspRate, sampleRate); right.SetRates(DspRate, sampleRate);
    }

    private void Push(short l, short r)
    {
        one[0] = l * (1f / 32768f); left.Write(one);
        one[0] = r * (1f / 32768f); right.Write(one);
    }

    public void Write(int reg, byte v) => Dsp.Write(reg, v);
    public void WriteRam(int addr, ReadOnlySpan<byte> bytes) => bytes.CopyTo(Aram.AsSpan(addr));

    public void Run(int n) { for (int i = 0; i < n; i++) Dsp.RunSample(); Samples += n; }

    /// <summary>Fills left[off..off+n) and right (null: mono = the left side), as chip.js's render.</summary>
    public void Render(float[] l, float[]? r, int off, int n)
    {
        while (left.Available < n) Run(1);
        left.Read(l.AsSpan(off, n));
        if (r != null) right.Read(r.AsSpan(off, n));
        else { if (scratch.Length < n) scratch = new float[n]; right.Read(scratch.AsSpan(0, n)); }
    }
}
