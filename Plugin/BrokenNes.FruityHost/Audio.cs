using System.Text;

namespace BrokenNes.FruityHost;

/// <summary>WAV I/O and the measurements the certification is built on.</summary>
public static class Audio
{
    // ---- WAV ----

    /// <summary>Writes 32-bit float WAV, interleaved.</summary>
    public static void WriteWav(string path, float[] interleaved, int channels, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = interleaved.Length * 4;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)3); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 4); w.Write((short)(channels * 4)); w.Write((short)32);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
        var b = new byte[bytes];
        Buffer.BlockCopy(interleaved, 0, b, 0, bytes);
        w.Write(b);
    }

    /// <summary>Reads PCM 16/24/32-bit or float WAV (also WAVE_FORMAT_EXTENSIBLE) into interleaved floats.</summary>
    public static (float[] Data, int Channels, int Rate) ReadWav(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 44 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "WAVE")
            throw new InvalidDataException("not a WAV file");
        int pos = 12, fmtTag = 0, ch = 0, rate = 0, bits = 0, dataPos = -1, dataLen = 0;
        while (pos + 8 <= d.Length)
        {
            string id = Encoding.ASCII.GetString(d, pos, 4);
            int len = BitConverter.ToInt32(d, pos + 4);
            if (id == "fmt ")
            {
                fmtTag = BitConverter.ToUInt16(d, pos + 8); ch = BitConverter.ToUInt16(d, pos + 10);
                rate = BitConverter.ToInt32(d, pos + 12); bits = BitConverter.ToUInt16(d, pos + 22);
                if (fmtTag == 0xFFFE && len >= 26) fmtTag = BitConverter.ToUInt16(d, pos + 32);   // extensible: the sub-format GUID starts with the tag
            }
            else if (id == "data") { dataPos = pos + 8; dataLen = Math.Min(len < 0 ? int.MaxValue : len, d.Length - dataPos); break; }
            pos += 8 + len + (len & 1);
        }
        if (dataPos < 0 || ch == 0) throw new InvalidDataException("WAV has no fmt/data");
        int bytesPer = bits / 8, n = dataLen / bytesPer;
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            int o = dataPos + i * bytesPer;
            x[i] = (fmtTag, bits) switch
            {
                (3, 32) => BitConverter.ToSingle(d, o),
                (1, 16) => BitConverter.ToInt16(d, o) / 32768f,
                (1, 24) => ((d[o] | (d[o + 1] << 8) | (d[o + 2] << 16)) << 8 >> 8) / 8388608f,
                (1, 32) => BitConverter.ToInt32(d, o) / 2147483648f,
                _ => throw new NotSupportedException($"WAV format tag {fmtTag}, {bits} bits"),
            };
        }
        return (x, ch, rate);
    }

    public static float[] Mono(float[] interleaved, int channels)
    {
        if (channels == 1) return interleaved;
        var m = new float[interleaved.Length / channels];
        for (int i = 0; i < m.Length; i++)
        {
            float s = 0;
            for (int c = 0; c < channels; c++) s += interleaved[i * channels + c];
            m[i] = s / channels;
        }
        return m;
    }

    public static float[] Channel(float[] interleaved, int channels, int c)
    {
        var m = new float[interleaved.Length / channels];
        for (int i = 0; i < m.Length; i++) m[i] = interleaved[i * channels + c];
        return m;
    }

    // ---- measurements ----

    public static double Rms(float[] x, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(x.Length, to);
        if (to <= from) return 0;
        double s = 0;
        for (int i = from; i < to; i++) s += (double)x[i] * x[i];
        return Math.Sqrt(s / (to - from));
    }

    public static double Peak(float[] x, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(x.Length, to);
        double p = 0;
        for (int i = from; i < to; i++) p = Math.Max(p, Math.Abs(x[i]));
        return p;
    }

    /// <summary>Mean frequency (Hz) of a periodic signal over [from, to): rising zero crossings of the mean-removed signal,
    /// with hysteresis against noise and linear interpolation of each crossing. NaN when the window is too quiet or has
    /// fewer than 3 crossings. Accuracy ~ 1 sample / (cycles in the window x samples per cycle).</summary>
    public static double Frequency(float[] x, int rate, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(x.Length, to);
        if (to - from < 16) return double.NaN;
        // Cross at the midpoint between the wave's own high and low levels, with hysteresis relative to the
        // full swing: robust to duty cycle (a 12.5% pulse is far from zero-mean) and to the slow droop the
        // APU's DC filter puts on flat sections, which a mean-based threshold mistakes for extra cycles.
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = from; i < to; i++) { lo = Math.Min(lo, x[i]); hi = Math.Max(hi, x[i]); }
        double swing = hi - lo;
        if (swing < 0.01) return double.NaN;
        double mid = (hi + lo) / 2, hyst = swing * 0.25;
        bool armed = false;
        double first = double.NaN, last = double.NaN;
        int count = 0;
        double prev = x[from] - mid;
        for (int i = from + 1; i < to; i++)
        {
            double cur = x[i] - mid;
            if (cur < -hyst) armed = true;
            if (armed && prev < 0 && cur >= 0)
            {
                double t = i - 1 + (0 - prev) / (cur - prev);   // sample position of the crossing
                if (count == 0) first = t;
                last = t;
                count++;
                armed = false;
            }
            prev = cur;
        }
        if (count < 3) return double.NaN;
        return (count - 1) * rate / (last - first);
    }

    public static double Cents(double hz, double refHz) => 1200 * Math.Log2(hz / refHz);

    /// <summary>Frequency per window: (centre time in ms, Hz or NaN).</summary>
    public static List<(double Ms, double Hz)> Track(float[] x, int rate, double fromMs, double toMs, double windowMs = 40, double hopMs = 10)
    {
        var list = new List<(double, double)>();
        int win = (int)(windowMs * rate / 1000);
        for (double t = fromMs; t + windowMs <= toMs; t += hopMs)
        {
            int a = (int)(t * rate / 1000);
            list.Add((t + windowMs / 2, Frequency(x, rate, a, a + win)));
        }
        return list;
    }
}

/// <summary>What the NES sound chip does with a pitch, so tests can say exactly what frequency to expect.</summary>
public static class Nes
{
    public const double CpuHz = 1789773.0;

    public static double KeyHz(double key) => 440.0 * Math.Pow(2.0, (key - 69.0) / 12.0);

    /// <summary>The period BrokenNes2 writes for a pitch (cents from C5) on a pulse (16) or triangle (32) channel.</summary>
    public static int Period(double cents, bool triangle, int coarseCents = 0)
    {
        double hz = KeyHz(60.0 + (cents + coarseCents) / 100.0);
        int p = (int)Math.Round(CpuHz / ((triangle ? 32.0 : 16.0) * hz) - 1.0);
        return triangle ? Math.Clamp(p, 2, 0x7FF) : Math.Clamp(p, 8, 0x7FF);
    }

    /// <summary>The frequency the chip really produces for a period.</summary>
    public static double PeriodHz(int period, bool triangle) => CpuHz / ((triangle ? 32.0 : 16.0) * (period + 1.0));
}
