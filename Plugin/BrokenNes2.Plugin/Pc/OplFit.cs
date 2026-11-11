// Fitting an OPL2 patch to a sound (2026-11-10, composing gaps part C): given a target note (mono PCM, its rate, its fundamental), search the OPL2's 2-operator patch space for the
// patch whose note on the AdLib (ymfm, the x86 console's own Opl2) sounds most like it, and say how alike they are.
//
// Features compared (both sides measured the same way):
//   spectrum  the first 16 harmonics' levels (dB, relative to the loudest), by a DFT at k x f0 over the held part of the note (after the attack: 60 ms in to 90% of the note)
//   envelope  the RMS in 24 equal frames over the note, each relative to the loudest frame
// Distance = 0.6 x the mean harmonic difference (dB, each capped at 40, / 40) + 0.4 x the mean envelope difference. Similarity = 100 x (1 - distance), 0-100 %.
// Search: every patch of the bank (OplBank: the games' own, the YM2413's, the designed GM bank), the best 4 refined by coordinate descent over the fields (multiples, levels,
// feedback, connection, waveforms, the four rates of both operators), then random two-field moves, deterministic (seeded). Uses only the plugin's PcVoice: pure, no UI, no files.
using NesEmulator.X86;

namespace BrokenNes2.Pc;

public sealed record OplFitResult(byte[] Patch, double Similarity, double SpectrumDb, double EnvelopeDiff, string StartedFrom, int Renders);

public static class OplFit
{
    public const int Harmonics = 16, Frames = 24;

    /// <summary>Deliberate bugs for the tests: 1 = the candidate's spectrum is measured an octave up (the fit chases the wrong partials).</summary>
    public static int Mutant;

    public sealed record Features(double[] Spectrum, double[] Envelope);

    public static Features Measure(ReadOnlySpan<float> pcm, int rate, double f0)
    {
        var spec = new double[Harmonics];
        int a = Math.Min(pcm.Length - 1, (int)(rate * 0.06)), b = Math.Max(a + 1, (int)(pcm.Length * 0.9));
        double max = 1e-12;
        for (int k = 1; k <= Harmonics; k++)
        {
            double f = f0 * k;
            if (f >= rate / 2.0) { spec[k - 1] = 0; continue; }
            double re = 0, im = 0, w = 2 * Math.PI * f / rate;
            for (int i = a; i < b; i++) { re += pcm[i] * Math.Cos(w * i); im += pcm[i] * Math.Sin(w * i); }
            spec[k - 1] = Math.Sqrt(re * re + im * im) / (b - a);
            max = Math.Max(max, spec[k - 1]);
        }
        for (int k = 0; k < Harmonics; k++) spec[k] = Math.Max(-60, 20 * Math.Log10(Math.Max(1e-12, spec[k]) / max));
        var env = new double[Frames];
        double emax = 1e-12;
        int len = Math.Max(Frames, pcm.Length);
        for (int fr = 0; fr < Frames; fr++)
        {
            int s0 = fr * pcm.Length / Frames, s1 = Math.Max(s0 + 1, (fr + 1) * pcm.Length / Frames);
            double sum = 0; for (int i = s0; i < s1 && i < pcm.Length; i++) sum += pcm[i] * pcm[i];
            env[fr] = Math.Sqrt(sum / (s1 - s0)); emax = Math.Max(emax, env[fr]);
        }
        for (int fr = 0; fr < Frames; fr++) env[fr] /= emax;
        return new Features(spec, env);
    }

    public static (double Distance, double SpecDb, double Env) Compare(Features t, Features c)
    {
        double ds = 0, de = 0;
        for (int k = 0; k < Harmonics; k++) ds += Math.Min(40, Math.Abs(t.Spectrum[k] - c.Spectrum[k]));
        ds /= Harmonics;
        for (int f = 0; f < Frames; f++) de += Math.Abs(t.Envelope[f] - c.Envelope[f]);
        de /= Frames;
        return (0.6 * ds / 40 + 0.4 * de, ds, de);
    }

    /// <summary>The patch's note on the AdLib (ymfm) at <paramref name="f0"/>, held for <paramref name="samples"/> samples at 44.1 kHz.</summary>
    public static float[] Render(byte[] patch, double f0, int samples, bool snare = false)
    {
        var v = new PcVoice("ADLIB");
        v.SetPatch(patch, snare);
        v.NoteOn(f0, 127);
        var o = new float[samples];
        v.Render(o, 44100);
        return o;
    }

    public static OplFitResult Fit(ReadOnlySpan<float> target, int rate, double f0, int seed = 1, int budget = 1200)
    {
        // the target at 44.1 kHz length: the candidates are rendered for the same time
        int n = (int)Math.Min(44100L * 2, (long)target.Length * 44100 / rate);
        var tf = Measure(target, rate, f0);
        int renders = 0;
        double Score(byte[] p)
        {
            renders++;
            var pcm = Render(p, Mutant == 1 ? f0 * 2 : f0, n);
            return Compare(tf, Measure(pcm, 44100, f0)).Distance;
        }
        var scored = new List<(double d, byte[] p, string name)>();
        foreach (var bp in OplBank.Patches) if (!bp.Snare) scored.Add((Score(bp.Regs), (byte[])bp.Regs.Clone(), bp.Name));
        scored.Sort((x, y) => x.d.CompareTo(y.d));
        var best = scored[0];
        var rng = new Random(seed);
        int[] tune = [4, 16, 6, 18, 24, 25, 7, 19, 0, 12, 1, 13, 2, 14, 3, 15, 5, 17];   // OplBank.Fields: MULTs, TLs, feedback, connection, waves, rates, KSLs
        foreach (var start in scored.Take(4))
        {
            var cur = (byte[])start.p.Clone(); double cd = start.d;
            bool improved = true;
            for (int pass = 0; pass < 6 && improved && renders < budget; pass++)
            {
                improved = false;
                foreach (int f in tune)
                {
                    int max = (1 << OplBank.Fields[f].Bits) - 1, v0 = OplBank.GetField(cur, f);
                    foreach (int step in max > 7 ? new[] { -4, -1, 1, 4 } : new[] { -1, 1 })
                    {
                        if (renders >= budget) break;
                        int v = v0 + step; if (v < 0 || v > max) continue;
                        var c = (byte[])cur.Clone(); OplBank.SetField(c, f, v);
                        double d = Score(c);
                        if (d < cd) { cd = d; cur = c; v0 = v; improved = true; }
                    }
                }
            }
            if (cd < best.d) best = (cd, cur, start.name);
        }
        // random two-field moves around the best
        var bp2 = (byte[])best.p.Clone(); double bd = best.d;
        while (renders < budget)
        {
            var c = (byte[])bp2.Clone();
            for (int j = 0; j < 2; j++) { int f = tune[rng.Next(tune.Length)]; int max = (1 << OplBank.Fields[f].Bits) - 1; OplBank.SetField(c, f, rng.Next(max + 1)); }
            double d = Score(c);
            if (d < bd) { bd = d; bp2 = c; }
        }
        var fin = Compare(tf, Measure(Render(bp2, f0, n), 44100, f0));
        return new OplFitResult(bp2, Math.Round(100 * Math.Clamp(1 - fin.Distance, 0, 1), 1), fin.SpecDb, fin.Env, best.name, renders);
    }

    public static string Hex(byte[] p) => string.Join(" ", p.Select(b => b.ToString("X2")));
}
