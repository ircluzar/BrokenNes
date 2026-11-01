using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// Shared pieces of the Instrument Runaway lab (round 6): running a session and keeping its sound, a phase-insensitive distance between two sounds, and the loading of a game.
/// The lab measures two things for a corruption: what the corrupted GAME sounds like against a clean run from the same machine, and what Instrument Runaway renders from the corrupted machine against what it
/// renders from the clean one. Nothing here listens to anything: every figure is a hash or a number computed from PCM.
/// </summary>
internal static class LabCommon
{
    /// <summary>Runs a session for <paramref name="frames"/> frames and returns its sound (interleaved stereo shorts at the session's rate), taking the pad from <paramref name="pad"/> (null: nothing held).</summary>
    public static short[] RunPcm(IConsoleSession s, int frames, Func<int, PadButtons>? pad = null, int firstFrame = 0)
    {
        var all = new List<short>(frames * 1100);
        var buf = new short[32768];
        for (int i = 0; i < frames; i++)
        {
            s.SetPad(0, pad?.Invoke(firstFrame + i) ?? PadButtons.None);
            s.RunFrame();
            int n;
            while ((n = s.ReadSamples(buf)) > 0) all.AddRange(new ArraySegment<short>(buf, 0, n));
        }
        return all.ToArray();
    }

    /// <summary>Drops whatever sound a session has queued (so two runs compared from a common point do not carry the sound of how they got there).</summary>
    public static void Flush(IConsoleSession s)
    {
        var buf = new short[32768];
        while (s.ReadSamples(buf) > 0) { }
    }

    public static float[] ToMono(short[] stereo)
    {
        var m = new float[stereo.Length / 2];
        for (int i = 0; i < m.Length; i++) m[i] = (stereo[2 * i] + stereo[2 * i + 1]) * (0.5f / 32768f);
        return m;
    }

    public static double Rms(float[] x, int from = 0, int count = -1)
    {
        if (count < 0) count = x.Length - from;
        if (count <= 0) return 0;
        double s = 0;
        for (int i = from; i < from + count; i++) s += x[i] * (double)x[i];
        return Math.Sqrt(s / count);
    }

    public static double Peak(float[] x) { double p = 0; foreach (float v in x) p = Math.Max(p, Math.Abs(v)); return p; }

    public static ulong Hash(float[] x)
    {
        ulong h = 1469598103934665603UL;
        foreach (float v in x) h = (h ^ (uint)BitConverter.SingleToInt32Bits(v)) * 1099511628211UL;
        return h;
    }

    public static ulong Hash(short[] x)
    {
        ulong h = 1469598103934665603UL;
        foreach (short v in x) h = (h ^ (ushort)v) * 1099511628211UL;
        return h;
    }

    // ---- a distance between two sounds that does not care about phase or small shifts in time ----

    private const int Fft = 1024, Hop = 512, Bands = 20;

    /// <summary>
    /// How different two sounds look: the mean, over short windows (1024 samples, hop 512) and 20 log-spaced bands between 60 Hz and the Nyquist frequency, of the absolute difference of the natural log of the band energy
    /// (with a floor of 1e-10). 0 is identical spectra over time; two unrelated loud sounds of similar level score about 1 to 3; a loud sound against silence scores about 10 to 20. Sounds of different length are compared over the shorter.
    /// </summary>
    public static double SpectralDistance(float[] a, float[] b, int rate)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n < Fft) return 0;
        var ea = new double[Bands]; var eb = new double[Bands];
        double sum = 0; long count = 0;
        var wa = new double[Fft]; var wb = new double[Fft];
        for (int at = 0; at + Fft <= n; at += Hop)
        {
            BandEnergies(a, at, rate, ea, wa);
            BandEnergies(b, at, rate, eb, wb);
            for (int k = 0; k < Bands; k++) { sum += Math.Abs(Math.Log(ea[k] + 1e-10) - Math.Log(eb[k] + 1e-10)); count++; }
        }
        return count == 0 ? 0 : sum / count;
    }

    /// <summary>The band energies of the whole sound, for a coarse look at what it is made of (used to say where a sound's energy sits).</summary>
    public static double[] AverageBands(float[] a, int rate)
    {
        var avg = new double[Bands]; var e = new double[Bands]; var w = new double[Fft];
        int windows = 0;
        for (int at = 0; at + Fft <= a.Length; at += Hop) { BandEnergies(a, at, rate, e, w); for (int k = 0; k < Bands; k++) avg[k] += e[k]; windows++; }
        if (windows > 0) for (int k = 0; k < Bands; k++) avg[k] /= windows;
        return avg;
    }

    private static readonly double[] hann = Enumerable.Range(0, Fft).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Fft)).ToArray();

    private static void BandEnergies(float[] x, int at, int rate, double[] bands, double[] re)
    {
        var im = new double[Fft];
        for (int i = 0; i < Fft; i++) { re[i] = x[at + i] * hann[i]; im[i] = 0; }
        FftInPlace(re, im);
        Array.Clear(bands);
        double nyq = rate / 2.0, lo = 60.0;
        for (int bin = 1; bin < Fft / 2; bin++)
        {
            double hz = bin * (double)rate / Fft;
            if (hz < lo) continue;
            int band = (int)(Bands * Math.Log(hz / lo) / Math.Log(nyq / lo));
            if (band >= Bands) band = Bands - 1;
            bands[band] += (re[bin] * re[bin] + im[bin] * im[bin]) / (Fft * Fft);
        }
    }

    private static void FftInPlace(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k, v = i + k + len / 2;
                    double xr = re[v] * cr - im[v] * ci, xi = re[v] * ci + im[v] * cr;
                    re[v] = re[u] - xr; im[v] = im[u] - xi;
                    re[u] += xr; im[u] += xi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    /// <summary>The frequency (Hz) of the strongest spectral peak over the whole sound (a coarse pitch check), 0 for silence.</summary>
    public static double DominantHz(float[] a, int rate)
    {
        if (a.Length < 4096 || Rms(a) < 1e-6) return 0;
        int n = 1; while (n * 2 <= a.Length && n < 1 << 16) n *= 2;
        var re = new double[n]; var im = new double[n];
        for (int i = 0; i < n; i++) re[i] = a[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
        FftInPlace(re, im);
        int best = 1; double bv = 0;
        for (int k = 1; k < n / 2; k++) { double m = re[k] * re[k] + im[k] * im[k]; if (m > bv) { bv = m; best = k; } }
        return best * (double)rate / n;
    }

    public static byte[] ReadRom(string path, out ConsoleKind kind, out string inner)
    {
        var rom = RomDetect.Unwrap(File.ReadAllBytes(path), path, out inner);
        kind = RomDetect.Detect(rom, inner) ?? NesEmulator.Sega.SegaRomFormat.Detect(rom, inner) ?? throw new InvalidDataException("not a ROM any console here plays: " + path);
        return rom;
    }

    public static string F(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
