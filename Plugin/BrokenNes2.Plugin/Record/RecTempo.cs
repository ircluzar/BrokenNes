// Round 8 (record): the game's tempo, from when its notes start. A SNES sound driver advances its song on a timer (Nintendo's N-SPC: a tick each time an 8-bit accumulator fed by the tempo
// overflows; Rare's and the others' differ in detail), so the key-ons of a song fall on a grid: the shortest note value the song uses (the "tatum": a 16th, an 8th, a triplet), give or take
// the driver's tick. Finding the grid:
//   1. the onsets (key-ons less than 4 ms apart are one onset, weighted by how many voices it keys);
//   2. for each candidate period s, how well the onsets line up on a grid of that period at the best phase: the length of the mean of e^(2 pi i t / s) (1 = all on the grid); the jitter
//      of the driver's tick lowers it for short periods, notes off the grid lower it for long ones: the best period is the tatum; refined by a fine scan around the peak;
//   3. the confidence: the share of the onset weight within 8 ms (or 6 % of the tatum) of the grid. Under 85 % the whole recording has no single tempo: it is cut into windows of 12 s, and
//      stretches of windows that agree on a grid become the segments of a tempo map (a song change, a "hurry up"); if they do not cover 70 % of the notes there is no tempo, the notes keep their
//      exact times at FL's tempo and the window says so (a corrupted driver, a free-time passage);
//   4. the beat. Onsets alone cannot tell a song at 72 BPM in 16th notes from the same song at 144 BPM in 8ths, so the rule is explicit and stated: the beat is the tatum times 2^n (or 3 x 2^n
//      when the onsets line up on three-tatum groups clearly better than on two- or four-tatum ones) that falls in 80-160 BPM. A song notated outside that range comes out at twice or half
//      its notated tempo, with the bar lines still on its grid. Bars of four beats start at the beat that carries the most onset weight.
// The notes are never moved onto the grid: their times stay exactly as the game played them; the tempo only puts FL's bar lines in the right place.
namespace BrokenNes2.Record;

/// <summary>One stretch of constant tempo: from <c>Seconds</c> on, <c>Bpm</c>; a downbeat (the start of a bar) at <c>DownbeatSeconds</c>.</summary>
public readonly record struct TempoSegment(double Seconds, double Bpm, double DownbeatSeconds, double TatumSeconds);

public sealed class TempoResult
{
    /// <summary>A grid was found (Confidence at least <see cref="RecTempo.MinConfidence"/>, in one stretch or in segments).</summary>
    public bool Found;
    public double Bpm, TatumSeconds, BeatSeconds, FirstDownbeat;
    /// <summary>Tatums per beat (4: the grid is 16th notes, 2: 8ths, 3: triplet 8ths, 1: the beat itself, 0.5: notes no closer than two beats).</summary>
    public double TatumsPerBeat;
    /// <summary>The share of the onset weight on the grid (0..1) and the alignment strength at the tatum (the mean resultant length).</summary>
    public double Confidence, Alignment;
    public int Onsets;
    /// <summary>Stretches of 6 s that find the same grid on their own, of those with enough notes; the driver's update period the onsets fall on (0 none found).</summary>
    public int WindowsAgreeing, Windows;
    public double LatticeSeconds;
    public List<TempoSegment> Segments = new();
    public string Why = "";

    public string Describe() => Found
        ? $"{Bpm:0.00} BPM ({TatumName} grid): {Confidence * 100:0}% of the notes on it, {WindowsAgreeing} of {Windows} stretches of 6 s find it alone{(Segments.Count > 1 ? $", {Segments.Count - 1} tempo change{(Segments.Count == 2 ? "" : "s")}" : "")}"
        : $"no steady tempo: {Why}";

    public string TatumName => Math.Round(TatumsPerBeat * 2) switch { 8 => "16th-note", 4 => "8th-note", 6 => "triplet", 12 => "16th-triplet", 16 => "32nd-note", 2 => "beat", 1 => "half-note", _ => $"{TatumsPerBeat:0.##}-per-beat" };
}

public static class RecTempo
{
    public const double MinConfidence = 0.85, MergeSeconds = 0.004, WindowSeconds = 12, MinCover = 0.7, BeatLow = 80, BeatHigh = 160;

    /// <summary>Onsets (seconds) and weights from note start times.</summary>
    public static (double[] T, double[] W) Onsets(IEnumerable<double> starts)
    {
        var s = starts.OrderBy(x => x).ToArray();
        var t = new List<double>(); var w = new List<double>();
        foreach (double x in s)
        {
            if (t.Count > 0 && x - t[^1] < MergeSeconds) { w[^1] += 1; continue; }
            t.Add(x); w.Add(1);
        }
        return (t.ToArray(), w.ToArray());
    }

    /// <summary>How well the onsets line up on a grid of period <paramref name="s"/> (0..1), and the grid's phase (seconds, 0..s).</summary>
    public static double Alignment(double[] t, double[] w, double s, out double phase, int from = 0, int to = -1)
    {
        if (to < 0) to = t.Length;
        double sx = 0, sy = 0, sw = 0, k = 2 * Math.PI / s;
        for (int i = from; i < to; i++) { double a = t[i] * k; sx += w[i] * Math.Cos(a); sy += w[i] * Math.Sin(a); sw += w[i]; }
        if (sw <= 0) { phase = 0; return 0; }
        double ang = Math.Atan2(sy, sx);
        if (ang < 0) ang += 2 * Math.PI;
        phase = ang / k;
        return Math.Sqrt(sx * sx + sy * sy) / sw;
    }

    /// <summary>The driver's own update period as the onsets show it (they all fall on multiples of it), or 0 when they do not: a coarse driver (Rare's ticks at 12.5 ms) places a note up to a
    /// whole tick after its ideal time, so the grid's tolerance must cover that.</summary>
    [ThreadStatic] private static double lattice;

    private static double Tol(double s) => Math.Max(0.008, Math.Max(0.06 * s, 0.6 * lattice));

    /// <summary>The longest period between 4 and 20 ms on which nearly every onset falls (mean resultant 0.97 of the best), or 0.</summary>
    public static double Lattice(double[] t, double[] w)
    {
        double bestR = 0;
        var rs = new List<(double S, double R)>();
        for (double s = 0.004; s <= 0.020; s *= 1.002) { double r = Alignment(t, w, s, out _); rs.Add((s, r)); bestR = Math.Max(bestR, r); }
        if (bestR < 0.9) return 0;
        double longest = 0;
        foreach (var (s, r) in rs) if (r >= 0.97 * bestR) longest = s;
        return longest;
    }

    /// <summary>The share of the onset weight within the tolerance of a grid of period <paramref name="s"/> and phase <paramref name="phase"/>.</summary>
    public static double OnGrid(double[] t, double[] w, double s, double phase, int from = 0, int to = -1)
    {
        if (to < 0) to = t.Length;
        double tol = Tol(s), on = 0, all = 0;
        for (int i = from; i < to; i++)
        {
            double x = (t[i] - phase) / s;
            double off = Math.Abs(x - Math.Round(x)) * s;
            all += w[i];
            if (off <= tol) on += w[i];
        }
        return all > 0 ? on / all : 0;
    }

    /// <summary>The best tatum between <paramref name="lo"/> and <paramref name="hi"/> seconds: a coarse scan (0.2 % steps) then a fine one around the peak.</summary>
    public static double BestPeriod(double[] t, double[] w, double lo, double hi, out double align, int from = 0, int to = -1)
    {
        double best = lo, bestR = -1;
        for (double s = lo; s <= hi; s *= 1.002)
        {
            double r = Alignment(t, w, s, out _, from, to);
            if (r > bestR + 1e-9) { bestR = r; best = s; }
        }
        double f0 = best / 1.003, f1 = best * 1.003;
        for (int pass = 0; pass < 3; pass++)
        {
            double step = (f1 - f0) / 60, b2 = best, r2 = bestR;
            for (double s = f0; s <= f1; s += step)
            {
                double r = Alignment(t, w, s, out _, from, to);
                if (r > r2) { r2 = r; b2 = s; }
            }
            best = b2; bestR = r2; f0 = best - 2 * step; f1 = best + 2 * step;
        }
        align = bestR;
        return best;
    }

    /// <summary>The beat for a tatum (see the rule at the top): the tatum times 2^n, or 3 x 2^n when the onsets favour three-tatum groups, in 80-160 BPM.</summary>
    public static double Beat(double[] t, double[] w, double tatum, int from = 0, int to = -1)
    {
        double r2 = Alignment(t, w, 2 * tatum, out _, from, to), r4 = Alignment(t, w, 4 * tatum, out _, from, to);
        double r3 = Alignment(t, w, 3 * tatum, out _, from, to), r6 = Alignment(t, w, 6 * tatum, out _, from, to);
        bool ternary = Math.Max(r3, r6) > 1.3 * Math.Max(r2, r4) && Math.Max(r3, r6) > 0.25;
        double beat = tatum * (ternary ? 3 : 1);
        while (60 / beat >= BeatHigh) beat *= 2;
        while (60 / beat < BeatLow) beat /= 2;
        return beat;
    }

    /// <summary>The time of the first downbeat at or after <paramref name="start"/>: the beat grid's phase is the tatum grid's moved to the tatum position that carries the most onset weight, the bar's the beat of four that does.</summary>
    public static double Downbeat(double[] t, double[] w, double tatum, double tatumPhase, double beat, double start, int from = 0, int to = -1)
    {
        if (to < 0) to = t.Length;
        double Weight(double period, double phase)
        {
            double tol = Tol(tatum), sum = 0;
            for (int i = from; i < to; i++) { double x = (t[i] - phase) / period; if (Math.Abs(x - Math.Round(x)) * period <= tol) sum += w[i]; }
            return sum;
        }
        int per = Math.Max(1, (int)Math.Round(beat / tatum));
        double beatPhase = tatumPhase, best = -1;
        for (int j = 0; j < per; j++) { double ph = tatumPhase + j * tatum, v = Weight(beat, ph); if (v > best) { best = v; beatPhase = ph; } }
        double bar = 4 * beat, barPhase = beatPhase; best = -1;
        for (int m = 0; m < 4; m++) { double ph = beatPhase + m * beat, v = Weight(bar, ph); if (v > best) { best = v; barPhase = ph; } }
        double first = barPhase + Math.Floor((start - barPhase) / bar) * bar;
        while (first < start - 1e-9) first += bar;
        return first;
    }

    public static TempoResult Estimate(IEnumerable<double> noteStarts, double lengthSeconds)
    {
        var (t, w) = Onsets(noteStarts);
        var res = new TempoResult { Onsets = t.Length };
        if (t.Length < 12) { res.Why = $"only {t.Length} note onsets"; return res; }
        double span = t[^1] - t[0];
        if (span < 3) { res.Why = $"the notes span {span:0.0} s"; return res; }
        double hi = Math.Min(1.2, span / 6);
        lattice = Lattice(t, w);
        res.LatticeSeconds = lattice;
        double tatum = BestPeriod(t, w, 0.06, hi, out double align);
        Alignment(t, w, tatum, out double phase);
        double conf = OnGrid(t, w, tatum, phase);
        res.TatumSeconds = tatum; res.Alignment = align; res.Confidence = conf;
        res.BeatSeconds = Beat(t, w, tatum);
        res.Bpm = 60 / res.BeatSeconds;
        res.TatumsPerBeat = res.BeatSeconds / tatum;
        (res.WindowsAgreeing, res.Windows) = Agreement(t, w, tatum, Math.Max(lengthSeconds, t[^1]));
        // a grid: most of the notes on it, or (music with notes set off it on purpose: delayed echo voices, flams) every stretch of the recording finding the same grid on its own
        bool agreed = res.Windows >= 3 && res.WindowsAgreeing >= 0.75 * res.Windows && conf >= 0.4;
        if ((conf >= MinConfidence || agreed) && align >= 0.5)
        {
            res.Found = true;
            res.FirstDownbeat = Downbeat(t, w, tatum, phase, res.BeatSeconds, 0);
            res.Segments.Add(new TempoSegment(0, res.Bpm, res.FirstDownbeat, tatum));
            return res;
        }
        // no single grid: windows, and stretches of windows that agree
        var segs = Windows(t, w, Math.Max(lengthSeconds, t[^1]));
        double covered = 0, on = 0, all = w.Sum();
        foreach (var (a, b, tat, ph) in segs) { double sw = 0; for (int i = a; i < b; i++) sw += w[i]; covered += sw; on += OnGrid(t, w, tat, ph, a, b) * sw; }
        if (segs.Count == 0 || covered / all < MinCover || on / Math.Max(1e-9, covered) < MinConfidence)
        {
            res.Why = $"the best grid ({res.Bpm:0.0} BPM) holds {conf * 100:0}% of the notes and {res.WindowsAgreeing} of {res.Windows} stretches of 6 s find it alone (needs {MinConfidence * 100:0}%, or 3 in 4 stretches)" + (segs.Count > 0 ? $"; stretches with a grid of their own cover {covered / all * 100:0}% (needs {MinCover * 100:0}%)" : "");
            return res;
        }
        res.Found = true;
        res.Confidence = on / covered;
        for (int k = 0; k < segs.Count; k++)
        {
            var (a, b, tat, ph) = segs[k];
            double beat = Beat(t, w, tat, a, b);
            double start = k == 0 ? 0 : t[a];
            res.Segments.Add(new TempoSegment(start, 60 / beat, Downbeat(t, w, tat, ph, beat, start, a, b), tat));
        }
        var s0 = res.Segments[0];
        res.Bpm = s0.Bpm; res.BeatSeconds = 60 / s0.Bpm; res.TatumSeconds = s0.TatumSeconds; res.TatumsPerBeat = res.BeatSeconds / s0.TatumSeconds; res.FirstDownbeat = s0.DownbeatSeconds;
        return res;
    }

    /// <summary>How many stretches of 6 s (with at least 12 onsets) find, on their own, the same grid as the whole recording (the same period, or twice, three or four times it or a half, third or quarter of it, within 0.6 %).</summary>
    public static (int Agreeing, int Windows) Agreement(double[] t, double[] w, double tatum, double length)
    {
        int agreeing = 0, windows = 0;
        for (double ws = 0; ws < length; ws += 6)
        {
            int a = Array.FindIndex(t, x => x >= ws), b = Array.FindIndex(t, x => x >= ws + 6);
            if (a < 0) break;
            if (b < 0) b = t.Length;
            if (b - a < 12) continue;
            windows++;
            double local = BestPeriod(t, w, 0.06, 1.0, out _, a, b);
            foreach (double f in new[] { 1, 2, 0.5, 3, 1 / 3.0, 4, 0.25 })
                if (Math.Abs(local / (tatum * f) - 1) < 0.006) { agreeing++; break; }
        }
        return (agreeing, windows);
    }

    /// <summary>Windows of <see cref="WindowSeconds"/> (half overlapping) with a grid of their own, merged into stretches that agree within 0.5 %: (first onset, end onset, tatum, phase) per stretch.</summary>
    private static List<(int A, int B, double Tatum, double Phase)> Windows(double[] t, double[] w, double length)
    {
        var good = new List<(double Start, int A, int B, double Tatum)>();
        for (double ws = 0; ws < length; ws += WindowSeconds / 2)
        {
            int a = Array.FindIndex(t, x => x >= ws), b = Array.FindIndex(t, x => x >= ws + WindowSeconds);
            if (a < 0) break;
            if (b < 0) b = t.Length;
            if (b - a < 12) continue;
            double tat = BestPeriod(t, w, 0.06, Math.Min(1.2, WindowSeconds / 6), out double al, a, b);
            Alignment(t, w, tat, out double ph, a, b);
            if (al < 0.5 || OnGrid(t, w, tat, ph, a, b) < MinConfidence) continue;
            good.Add((ws, a, b, tat));
        }
        var segs = new List<(int A, int B, double Tatum, double Phase)>();
        int i0 = 0;
        while (i0 < good.Count)
        {
            int i1 = i0;
            while (i1 + 1 < good.Count && Math.Abs(good[i1 + 1].Tatum / good[i0].Tatum - 1) < 0.005 && good[i1 + 1].A <= good[i1].B) i1++;
            int a = good[i0].A, b = good[i1].B;
            if (segs.Count > 0 && a < segs[^1].B) a = segs[^1].B;            // (windows overlap: a new stretch starts where the last one ended)
            if (b - a >= 12)
            {
                double tat = BestPeriod(t, w, good[i0].Tatum * 0.995, good[i0].Tatum * 1.005, out _, a, b);
                Alignment(t, w, tat, out double ph, a, b);
                segs.Add((a, b, tat, ph));
            }
            i0 = i1 + 1;
        }
        return segs;
    }
}
