using System;

namespace NesEmulator.Sega;

/// <summary>
/// The Genesis sound unit as Nano Genesis plays it (Nano/genesis/src/chip.js): Core's <see cref="Ym2612"/> (the Model 1 chip: ladder, slot pipeline) and
/// <see cref="Sn76489"/> (variant MegaDrive) mixed the way <c>md/MdSoundUnit.cs</c> mixes them: each FM sample a step of a held level into two
/// <see cref="BandLimitedMixer"/>s at the chip's 53,267 Hz (<c>IYm2612Sink.Sample</c>), the PSG's 16-bit samples plus <c>(int)(fm x 32768)</c>, clipped
/// (<c>MdSoundUnit.ReadSamples</c>). No Z80 and no bus: the instrument engine (<see cref="NanoGenesisEngine"/>) writes the registers itself, and the chips are
/// run just far enough for the samples asked for, the YM2612 one sample at a time, as the web library runs its port.
/// </summary>
public sealed class NanoGenesisSound : IYm2612Sink
{
    public const double MasterHz = 53693175.0;
    public const double PsgClockHz = MasterHz / 15;
    public const double YmRate = MasterHz / 1008;

    public readonly Sn76489 Psg;
    public readonly Ym2612 Ym;
    private readonly BandLimitedMixer fmLeft, fmRight;
    private long fmClock;
    private float lastL, lastR;
    private short[] psgBuf = new short[2 * 512];
    private float[] fl = new float[512], fr = new float[512];

    public NanoGenesisSound(int sampleRate)
    {
        Psg = new Sn76489(PsgClockHz, sampleRate, PsgVariant.MegaDrive);
        Ym = new Ym2612(ladder: true) { Sink = this };
        fmLeft = new BandLimitedMixer(YmRate, sampleRate);
        fmRight = new BandLimitedMixer(YmRate, sampleRate);
    }

    /// <summary>YM2612 samples made so far.</summary>
    public long YmSamples => Ym.Samples;

    public void PsgWrite(byte v) => Psg.Write(v);

    /// <summary>A YM2612 register (0x000-0x0FF part 1, 0x100-0x1FF part 2): the address port, then the data port, at the clock the chip has reached.</summary>
    public void YmWrite(int reg, byte v)
    {
        bool part2 = (reg & 0x100) != 0;
        Ym.Write(part2 ? 2 : 0, (byte)(reg & 0xFF), Ym.Clock);
        Ym.Write(part2 ? 3 : 1, v, Ym.Clock);
    }

    /// <summary>n YM2612 samples now (each one a step into the FM mixers), then the mixers told how far the stream has got.</summary>
    public void YmRun(int n)
    {
        if (n <= 0) return;
        Ym.RunTo((Ym.Samples + n) * Ym2612.ClocksPerSample);
        fmLeft.Advance(fmClock); fmRight.Advance(fmClock);
    }

    void IYm2612Sink.Sample(int left, int right)
    {
        float l = left * 1.0f * (1f / 32768f), r = right * 1.0f * (1f / 32768f);       // (MdSoundUnit: FmGain 1)
        fmClock++;
        if (l != lastL) { fmLeft.Step(fmClock, l - lastL); lastL = l; }
        if (r != lastR) { fmRight.Step(fmClock, r - lastR); lastR = r; }
    }

    /// <summary>Fills <paramref name="left"/>[off..off+n) and <paramref name="right"/> (null: mono = the left side) with the next n samples, as Nano/genesis/src/chip.js's render.</summary>
    public void Render(float[] left, float[]? right, int off, int n)
    {
        Psg.RunForSamples(n);
        while (fmLeft.Available < n) YmRun(1);
        if (psgBuf.Length < 2 * n) { psgBuf = new short[2 * n]; fl = new float[n]; fr = new float[n]; }
        int got = Psg.ReadSamples(psgBuf.AsSpan(0, 2 * n));
        if (got != 2 * n) throw new InvalidOperationException($"the PSG had {got / 2} of {n} frames");
        fmLeft.Read(fl.AsSpan(0, n)); fmRight.Read(fr.AsSpan(0, n));
        for (int i = 0; i < n; i++)
        {
            int l = psgBuf[2 * i] + (int)(fl[i] * 32768f);
            left[off + i] = (short)(l < -32768 ? -32768 : l > 32767 ? 32767 : l) / 32768f;
            if (right != null)
            {
                int r = psgBuf[2 * i + 1] + (int)(fr[i] * 32768f);
                right[off + i] = (short)(r < -32768 ? -32768 : r > 32767 ? 32767 : r) / 32768f;
            }
        }
    }
}
