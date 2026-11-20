using System;

namespace BrokenNes.Windows
{
    /// <summary>
    /// A streaming WSOLA time stretcher (mono float): makes more sound out of the same samples without changing the pitch, for an emulation
    /// running below full speed (the x86 PC in StarCraft / Age of Empires at 60-90 %): the queue would run dry every few frames and the
    /// device play silence (the "stuttering"); stretched, the sound plays on continuously, a little slower. Grains of 1024 samples (23 ms at
    /// 44.1 kHz) with Hann windows at 50 % overlap; each grain is the one within +-256 samples of its nominal place that best continues the last.
    /// </summary>
    internal sealed class AudioStretch
    {
        private const int N = 1024, Hs = N / 2, S = 256;
        private static readonly float[] Win = MakeWindow();
        private float[] _buf = new float[1 << 16];
        private int _len;              // samples in _buf
        private double _pos;           // the next grain's nominal start (in _buf)
        private int _nat = -1;         // where the last grain's natural continuation starts (-1: no grain yet)
        private readonly float[] _ola = new float[N];

        private static float[] MakeWindow()
        {
            var w = new float[N];
            for (int i = 0; i < N; i++) w[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / N);   // periodic Hann: two at 50 % overlap sum to 1
            return w;
        }

        public void Reset() { _len = 0; _pos = 0; _nat = -1; Array.Clear(_ola); }

        /// <summary>Takes <paramref name="count"/> samples, gives about <paramref name="factor"/> times as many (>= 1) into <paramref name="output"/> (grown as needed); returns the count.</summary>
        public int Process(float[] input, int count, double factor, ref float[] output)
        {
            if (_len + count > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + count));
            Array.Copy(input, 0, _buf, _len, count); _len += count;
            factor = Math.Clamp(factor, 1.0, 3.0);
            int o = 0;
            while (true)
            {
                int p = (int)_pos;
                int lo = Math.Max(0, p - S), hi = p + S;
                if (hi + N > _len || (_nat >= 0 && _nat + N > _len)) break;
                int best = p;
                if (_nat >= 0)
                {   // the candidate most like the natural continuation of the last grain (its first half, every 2nd sample)
                    double bestC = double.NegativeInfinity;
                    for (int k = lo; k <= hi; k++)
                    {
                        double c = 0;
                        for (int i = 0; i < Hs; i += 2) c += _buf[_nat + i] * _buf[k + i];
                        if (c > bestC) { bestC = c; best = k; }
                    }
                }
                for (int i = 0; i < N; i++) _ola[i] += Win[i] * _buf[best + i];
                if (o + Hs > output.Length) Array.Resize(ref output, Math.Max(output.Length * 2, o + Hs));
                Array.Copy(_ola, 0, output, o, Hs); o += Hs;
                Array.Copy(_ola, Hs, _ola, 0, N - Hs); Array.Clear(_ola, N - Hs, Hs);
                _nat = best + Hs;
                _pos += Hs / factor;
            }
            // drop what no grain will read again
            int keep = Math.Min(Math.Max(0, (int)_pos - S), _nat < 0 ? int.MaxValue : _nat);
            if (keep > 0)
            {
                Array.Copy(_buf, keep, _buf, 0, _len - keep); _len -= keep; _pos -= keep; if (_nat >= 0) _nat -= keep;
            }
            return o;
        }
    }
}
