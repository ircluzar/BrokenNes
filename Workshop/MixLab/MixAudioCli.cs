using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using NesEmulator;
using NesEmulator.Snes;

using NesEmulator.Mix;

namespace BrokenNes.Workshop.MixLab;

/// <summary>
/// MIX LAB audio commands.
///   snesaudio --rom game.sfc --apu SFC|NES|HLE [--nes-apu FIX] [--frames N] [--input ...] --wav out.wav
///   audiocmp  --a ref.wav --b test.wav [--png spectrogram.png] [--from s] [--seconds s]
/// NES-game audio is recorded by "--mixlab nes ... --wav out.wav".
/// </summary>
internal static class MixAudioCli
{
    public static int SnesAudio(Func<string, string, string> opt)
    {
        string rom = opt("rom", ""), wav = opt("wav", "out.wav");
        MixConfig.NesBackApu = opt("nes-apu", "FIX");
        int frames = int.Parse(opt("frames", "1200"));
        var script = SnesRunCli.ParseInput(opt("input", ""));
        var apu = SnesCores.CreateApu(opt("apu", "SFC"));
        var board = new BOARD_SFC(SnesCartridge.Load(File.ReadAllBytes(rom)), apu);
        var buf = new short[16384]; var all = new List<short>();
        ushort held = 0;
        for (int f = 0; f < frames; f++)
        {
            if (script.TryGetValue(f, out var h)) held = h;
            board.Pads[0] = held;
            board.RunFrame();
            int n; while ((n = board.Apu.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) all.Add((short)((buf[i] + buf[i + 1]) / 2));
        }
        WriteWav(wav, all.ToArray(), board.Apu.SampleRate);
        Console.WriteLine($"{Path.GetFileName(rom)} apu={board.Apu.CoreName} frames={frames} samples={all.Count:N0} rate={board.Apu.SampleRate} | {board.Apu.Describe()}");
        return 0;
    }

    public static void WriteWav(string path, short[] mono, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + mono.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(mono.Length * 2);
        foreach (var s in mono) w.Write(s);
    }

    /// <summary>Mono float samples + rate from a 16-bit PCM or 32-bit float WAV (channels averaged).</summary>
    public static (float[] s, int rate) ReadWav(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12, ch = 1, rate = 44100, bits = 16, fmt = 1; float[]? data = null;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4); int len = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") { fmt = BitConverter.ToInt16(b, pos + 8); ch = BitConverter.ToInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToInt16(b, pos + 22); }
            if (id == "data")
            {
                int bytesPer = bits / 8, frames = Math.Min(len, b.Length - pos - 8) / (bytesPer * ch); data = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float acc = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int o = pos + 8 + (i * ch + c) * bytesPer;
                        acc += fmt == 3 ? BitConverter.ToSingle(b, o) : BitConverter.ToInt16(b, o) / 32768f;
                    }
                    data[i] = acc / ch;
                }
            }
            pos += 8 + len + (len & 1);
        }
        return (data ?? Array.Empty<float>(), rate);
    }

    private static float[] Resample(float[] s, int from, int to)
    {
        if (from == to) return s;
        int n = (int)((long)s.Length * to / from); var r = new float[n];
        for (int i = 0; i < n; i++) { double x = (double)i * from / to; int k = (int)x; double t = x - k; r[i] = (float)(k + 1 < s.Length ? s[k] * (1 - t) + s[k + 1] * t : s[Math.Min(k, s.Length - 1)]); }
        return r;
    }

    /// <summary>Dominant pitch per window by normalised autocorrelation (0 = unvoiced).</summary>
    private static double[] Pitch(float[] s, int rate, int win, out double[] rms)
    {
        int n = s.Length / win; var p = new double[n]; rms = new double[n];
        int minLag = rate / 1500, maxLag = rate / 55;
        for (int w = 0; w < n; w++)
        {
            int o = w * win; double e = 0; for (int i = 0; i < win; i++) e += s[o + i] * s[o + i];
            rms[w] = Math.Sqrt(e / win);
            if (rms[w] < 0.005 || o + win + maxLag > s.Length) continue;
            double best = 0; int bl = 0; var c = new double[maxLag + 1];
            for (int lag = minLag; lag <= maxLag; lag++) { double a = 0; for (int i = 0; i < win; i++) a += s[o + i] * s[o + i + lag]; c[lag] = a / e; if (c[lag] > best) { best = c[lag]; bl = lag; } }
            if (best < 0.45) continue;
            for (int lag = minLag; lag <= maxLag; lag++) if (c[lag] >= 0.9 * best) { bl = lag; break; }
            p[w] = (double)rate / bl;
        }
        return p;
    }

    public static int AudioCmp(Func<string, string, string> opt)
    {
        var (a, ra) = ReadWav(opt("a", "")); var (b, rb) = ReadWav(opt("b", ""));
        const int rate = 32000; a = Resample(a, ra, rate); b = Resample(b, rb, rate);
        int from = (int)(double.Parse(opt("from", "0")) * rate), len = (int)(double.Parse(opt("seconds", "1e9")) * rate);
        a = a.Skip(from).Take(len).ToArray(); b = b.Skip(from).Take(len).ToArray();
        int win = 1280;                          // 40 ms
        var pa = Pitch(a, rate, win, out var ea); var pb = Pitch(b, rate, win, out var eb);
        int n = Math.Min(pa.Length, pb.Length), both = 0, same = 0, sameClass = 0, loudA = 0, loudB = 0, vA = 0, vB = 0;
        for (int i = 0; i < n; i++)
        {
            if (ea[i] > 0.005) loudA++; if (eb[i] > 0.005) loudB++; if (pa[i] > 0) vA++; if (pb[i] > 0) vB++;
            if (pa[i] > 0 && pb[i] > 0)
            {
                both++; double st = 12 * Math.Log2(pb[i] / pa[i]);
                if (Math.Abs(st) <= 1) same++;
                double m = ((st % 12) + 12) % 12; if (m <= 1 || m >= 11) sameClass++;
            }
        }
        double Db(float[] x) => 20 * Math.Log10(Math.Sqrt(x.Length == 0 ? 0 : x.Average(v => (double)v * v)) + 1e-9);
        Console.WriteLine($"windows={n} (40 ms) | A: {Db(a):F1} dBFS, sounding {100.0 * loudA / Math.Max(n, 1):F0}%, pitched {100.0 * vA / Math.Max(n, 1):F0}% | B: {Db(b):F1} dBFS, sounding {100.0 * loudB / Math.Max(n, 1):F0}%, pitched {100.0 * vB / Math.Max(n, 1):F0}%");
        Console.WriteLine($"both pitched: {both} windows | same note (+-1 semitone): {100.0 * same / Math.Max(both, 1):F0}% | same note in any octave: {100.0 * sameClass / Math.Max(both, 1):F0}%");
        string png = opt("png", "");
        if (png != "") Spectrograms(png, a, b, rate, opt("label-a", "A"), opt("label-b", "B"));
        return 0;
    }

    // ---------------------------------------------------------------- spectrogram (log frequency, 60 Hz - 8 kHz)
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); } }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len; double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    double ur = re[i + k], ui = im[i + k], vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci, vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
                    re[i + k] = ur + vr; im[i + k] = ui + vi; re[i + k + len / 2] = ur - vr; im[i + k + len / 2] = ui - vi;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    private static void Spectrograms(string path, float[] a, float[] b, int rate, string la, string lb)
    {
        const int N = 2048, hop = 512, H = 180;
        int cols = Math.Min(Math.Max(a.Length, b.Length) / hop, 900);
        using var bmp = new Bitmap(cols, H * 2 + 44);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(14, 15, 20));
        using var font = new Font("Consolas", 10);
        void Draw(float[] s, int top, string label)
        {
            var re = new double[N]; var im = new double[N];
            for (int c = 0; c < cols; c++)
            {
                int o = c * hop; for (int i = 0; i < N; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N); re[i] = o + i < s.Length ? s[o + i] * w : 0; im[i] = 0; }
                Fft(re, im);
                for (int y = 0; y < H; y++)
                {
                    double f = 60 * Math.Pow(8000.0 / 60, (double)(H - 1 - y) / (H - 1)); int k = (int)(f * N / rate);
                    double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]); double db = 20 * Math.Log10(m + 1e-9);
                    double t = Math.Clamp((db + 10) / 60, 0, 1);
                    bmp.SetPixel(c, top + y, Color.FromArgb((int)(255 * Math.Min(1, t * 1.6)), (int)(255 * Math.Clamp(t * 1.4 - 0.3, 0, 1)), (int)(255 * Math.Clamp(0.4 + t - t * t * 1.4, 0, 1))));
                }
            }
            g.DrawString(label, font, Brushes.White, 4, top + 2);
        }
        Draw(a, 20, la); Draw(b, H + 40, lb);
        bmp.Save(path, ImageFormat.Png);
    }
}
