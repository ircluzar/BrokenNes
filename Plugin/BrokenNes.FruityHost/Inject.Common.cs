// Round 6 (inject): helpers shared by the feasibility probe and the push checks: running a real game from the user's library with a scripted pad, capturing its own audio, and a small FFT.
// The repository carries no ROM: a check that needs one reports "not tested" when the file is not there, never a pass.
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static class InjectRun
{
    public const string Smw = ImodelGames.Smw;
    public const string Sonic1 = ImodelGames.Sonic1;

    /// <summary>The scripted pad of the SNES goldens (Workshop/SnesBenchCli.cs): title, file select, Yoshi's House, the overworld at about frame 4000.</summary>
    public const string SmwScript = "500:Start,508:,700:A,708:,900:A,908:,1100:A,1108:,1300:A,1308:,1600:B,1610:,1650:A,1660:,1700:B,1710:,1750:A,1760:,1800:B,1810:,1850:A,1860:,1900:B,1910:,1950:A,1960:,2000:B,2010:,2050:A,2060:,2100:B,2110:,2150:A,2160:,2200:B,2210:,2250:A,2260:,2300:B,2310:,2350:A,2360:,2400:B,2410:,2450:A,2460:,2500:B,2510:,2550:A,2560:,2600:B,2610:,2650:A,2660:,2800:Right,3200:Right+B,3240:Right,3900:";

    /// <summary>Sonic the Hedgehog: Start on the title screen, then run right through Green Hill Zone.</summary>
    public const string SonicScript = "400:Start,420:,900:Right,1700:Right+A,1730:Right";

    public static IConsoleSession Create(ConsoleKind kind, byte[] rom, string fileName, string? apuId = null)
    {
        string apu = apuId ?? (kind == ConsoleKind.Genesis ? "MD" : CoreCatalog.Default(kind, CoreSlot.Apu));
        return ConsoleSessions.Create(kind, (byte[])rom.Clone(), CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), apu, null, fileName);
    }

    /// <summary>"frame:Buttons,frame:Buttons": from that frame on the buttons are held (empty releases them), as the goldens' scripts.</summary>
    public static Func<int, int> Pad(string script)
    {
        var steps = new List<(int Frame, int Bits)>();
        foreach (var part in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':');
            int frame = int.Parse(part[..colon]), bits = 0;
            foreach (var b in part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries))
                bits |= (int)Enum.Parse<PadButtons>(b, true);
            steps.Add((frame, bits));
        }
        return f => { int bits = 0; foreach (var (fr, b) in steps) if (fr <= f) bits = b; return bits; };
    }

    public static byte[]? Rom(string path, out string name) => ImodelGames.LoadRom(path, out name);

    /// <summary>Runs frames [from, to) of the game; the audio each frame made goes to <paramref name="pcm"/> (interleaved stereo shorts) when it is not null, and is dropped otherwise.</summary>
    public static void Frames(IConsoleSession s, int from, int to, Func<int, int> pad, List<short>? pcm = null, Action<int>? beforeFrame = null)
    {
        var buf = new short[1 << 16];
        for (int f = from; f < to; f++)
        {
            beforeFrame?.Invoke(f);
            s.SetPad(0, (PadButtons)pad(f));
            s.RunFrame();
            int n;
            while ((n = s.ReadSamples(buf)) > 0)
                if (pcm != null) for (int i = 0; i < n; i++) pcm.Add(buf[i]);
        }
    }

    public static ulong Hash(IEnumerable<short> pcm)
    {
        ulong h = 14695981039346656037UL;
        foreach (short v in pcm) h = (h ^ (ushort)v) * 1099511628211UL;
        return h;
    }

    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte v in data) h = (h ^ v) * 1099511628211UL;
        return h;
    }

    public static float[] Mono(IReadOnlyList<short> stereo, int fromFrame = 0, int toFrame = int.MaxValue)
    {
        int n = Math.Min(stereo.Count / 2, toFrame) - fromFrame;
        if (n <= 0) return [];
        var m = new float[n];
        for (int i = 0; i < n; i++) m[i] = (stereo[(fromFrame + i) * 2] + stereo[(fromFrame + i) * 2 + 1]) * (0.5f / 32768f);
        return m;
    }
}

/// <summary>An FFT-based look at a mono signal: the strongest spectral peak, and the energy in a band.</summary>
internal static class InjectSpectrum
{
    private static void Fft(double[] re, double[] im)
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
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    /// <summary>The power spectrum (bins 0..n/2) of a Hann-windowed slice, <paramref name="n"/> a power of two.</summary>
    public static double[] Power(float[] x, int from, int n)
    {
        var re = new double[n]; var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            re[i] = from + i < x.Length ? x[from + i] * w : 0;
        }
        Fft(re, im);
        var p = new double[n / 2 + 1];
        for (int k = 0; k < p.Length; k++) p[k] = re[k] * re[k] + im[k] * im[k];
        return p;
    }

    /// <summary>The frequency of the strongest peak between <paramref name="loHz"/> and <paramref name="hiHz"/>, refined by the parabola through the peak and its neighbours; NaN when the slice is quiet (peak power under <paramref name="floor"/>).</summary>
    public static double PeakHz(float[] x, int from, int n, int rate, double loHz = 60, double hiHz = 8000, double floor = 1e-3)
    {
        var p = Power(x, from, n);
        int a = Math.Max(2, (int)(loHz * n / rate)), b = Math.Min(p.Length - 2, (int)(hiHz * n / rate));
        int best = a;
        for (int k = a; k <= b; k++) if (p[k] > p[best]) best = k;
        if (p[best] < floor) return double.NaN;
        double l = Math.Log(p[best - 1] + 1e-30), c = Math.Log(p[best] + 1e-30), r = Math.Log(p[best + 1] + 1e-30);
        double denom = l - 2 * c + r, delta = Math.Abs(denom) > 1e-12 ? 0.5 * (l - r) / denom : 0;
        return (best + Math.Clamp(delta, -0.5, 0.5)) * rate / n;
    }

    /// <summary>Spectral centroid in Hz of a slice (a "brightness" figure: a sample byte edit moves it).</summary>
    public static double Centroid(float[] x, int from, int n, int rate)
    {
        var p = Power(x, from, n);
        double num = 0, den = 0;
        for (int k = 1; k < p.Length; k++) { num += p[k] * k; den += p[k]; }
        return den <= 0 ? 0 : num / den * rate / n;
    }

    public static double Rms(float[] x, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(x.Length, to);
        if (to <= from) return 0;
        double s = 0;
        for (int i = from; i < to; i++) s += (double)x[i] * x[i];
        return Math.Sqrt(s / (to - from));
    }
}
