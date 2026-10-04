using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// Converts the YM2413's sample stream (the chip's clock over 72: 49,716 Hz on an NTSC console) to the host's audio rate with a windowed-sinc filter. It is the rate converter of emu2413 v1.6.2 (MIT, Mitsutaka
/// Okazaki) with its two halves split apart: the chip's own <c>OPLL_calc</c> pulled chip samples on demand, and here the chip is run by the CPU clock, so the converter is told when a chip sample exists
/// (<see cref="Push"/>) and says when it can make an output sample (<see cref="TryRead"/>); the arithmetic (the 16-tap Blackman-windowed sinc table at 256 steps per tap, 12-bit amplitude, the double-precision
/// timer, the order of the chip samples each output consumes) is the original's, so for the same chip samples the output is the same as emu2413's at the same rate.
/// </summary>
/// <remarks>Latency is about 8 chip samples (160 microseconds). No allocation after construction.</remarks>
public sealed class Ym2413Resampler
{
    private const int LW = 16;            // taps; 8 either side of the output position
    private const int SincReso = 256;     // sinc table steps per tap
    private const int SincAmpBits = 12;

    private readonly double fRatio;       // input rate over output rate
    private readonly double inpStep, outStep;
    private readonly short[] sincTable = new short[SincReso * LW / 2];
    private readonly short[] buf = new short[LW];
    private double timer;
    private double outTime;
    private readonly short[] queue = new short[8];
    private int queued;

    /// <param name="inputHz">The chip sample rate (clock / 72).</param>
    /// <param name="outputHz">The host rate.</param>
    public Ym2413Resampler(double inputHz, int outputHz)
    {
        fRatio = inputHz / outputHz;
        outStep = inputHz; inpStep = outputHz;
        for (int i = 0; i < SincReso * LW / 2; i++)
        {
            double x = (double)i / SincReso;
            sincTable[i] = outputHz < inputHz
                ? (short)((1 << SincAmpBits) * WindowedSinc(x / fRatio) / fRatio)   // downsampling: the cutoff is the output's Nyquist
                : (short)((1 << SincAmpBits) * WindowedSinc(x));
        }
    }

    private static double Blackman(double x) => 0.42 - 0.5 * Math.Cos(2 * Math.PI * x) + 0.08 * Math.Cos(4 * Math.PI * x);
    private static double Sinc(double x) => x == 0.0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
    private static double WindowedSinc(double x) => Blackman(0.5 + 0.5 * x / (LW / 2)) * Sinc(x);

    public void Reset()
    {
        timer = 0; outTime = 0; queued = 0;
        Array.Clear(buf);
    }

    /// <summary>A chip sample is ready.</summary>
    public void Push(short sample)
    {
        if (queued < queue.Length) queue[queued++] = sample;
        else
        {
            // a consumer that stopped reading: keep the newest (never reached while the unit drains every run)
            Array.Copy(queue, 1, queue, 0, queue.Length - 1);
            queue[queue.Length - 1] = sample;
        }
    }

    /// <summary>
    /// Makes the next output sample if the chip samples it needs have been pushed (one or two per output at 44.1 kHz). False means "run the chip some more".
    /// </summary>
    public bool TryRead(out short output)
    {
        // how many chip samples this output consumes: the emu2413 loop "while (out_step > out_time) { out_time += inp_step; calc one chip sample }"
        int need = 0;
        double t = outTime;
        while (outStep > t) { t += inpStep; need++; }
        if (need > queued) { output = 0; return false; }
        for (int i = 0; i < need; i++)
        {
            for (int k = 0; k < LW - 1; k++) buf[k] = buf[k + 1];
            buf[LW - 1] = queue[i];
        }
        if (need > 0)
        {
            for (int i = need; i < queued; i++) queue[i - need] = queue[i];
            queued -= need;
        }
        outTime = t - outStep;

        timer += fRatio;
        double dn = timer - Math.Floor(timer);
        timer = dn;
        int sum = 0;
        for (int k = 0; k < LW; k++)
        {
            double x = (k - (LW / 2 - 1)) - dn;
            short idx = (short)(x * SincReso);
            if (idx < 0) idx = (short)-idx;
            sum += buf[k] * sincTable[Math.Min(SincReso * LW / 2 - 1, (int)idx)];
        }
        output = (short)(sum >> SincAmpBits);
        return true;
    }

    public void SaveState(BinaryWriter w)
    {
        w.Write(timer); w.Write(outTime); w.Write(queued);
        foreach (short s in buf) w.Write(s);
        foreach (short s in queue) w.Write(s);
    }

    public void LoadState(BinaryReader r)
    {
        timer = r.ReadDouble(); outTime = r.ReadDouble(); queued = r.ReadInt32();
        for (int i = 0; i < buf.Length; i++) buf[i] = r.ReadInt16();
        for (int i = 0; i < queue.Length; i++) queue[i] = r.ReadInt16();
    }
}
