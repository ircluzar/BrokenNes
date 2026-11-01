// Round 8 (record): the root note of a sample. A SNES sample has no "note": the S-DSP plays it at a rate set by the pitch register ($1000 = the sample's own rate, 32 kHz), and what musical
// note that is depends on the sample. To show the right notes on FL's piano roll, the transcription needs the sample's own pitch at $1000: its fundamental. Two independent clues:
//   1. the sample itself: its BRR data decoded exactly as the S-DSP decodes it, played through its loop, and the period found with YIN (de Cheveigné and Kawahara 2002: the cumulative-mean-
//      normalised difference function, its first dip under a threshold, refined by a parabola);
//   2. the game's notes: a driver that plays in equal temperament keys every note of an instrument at the same fraction of a semitone, so the notes' fractional cents say how the instrument
//      is tuned (a circular mean of the fractions).
// The preset's tuning is then set so that FL's note + pitch bend gives back the game's pitch register exactly, WHATEVER the estimate is: a wrong estimate moves the notes on the piano roll
// (an octave, say), never the pitch that plays. A sample with no clear pitch (a drum) keeps the plain convention ($1000 = C5) and says so.
namespace BrokenNes2.Record;

/// <summary>What the estimate found: the sample's fundamental at pitch $1000 (Hz, 0 when none), how periodic it is (0 none .. 1 a perfect repeat), and how it was found.</summary>
public readonly record struct RootEstimate(double Hz, double Clarity, string How)
{
    public bool Found => Hz > 0;
    /// <summary>The MIDI note (fractional) the sample plays at pitch $1000.</summary>
    public double Midi => Hz > 0 ? 69 + 12 * Math.Log2(Hz / 440.0) : 60;
}

public static class RecRoot
{
    public const int Rate = 32000;
    /// <summary>The lowest and highest fundamentals looked for (a SNES sample's pitch at $1000 is rarely outside 40 Hz to 6 kHz).</summary>
    public const double MinHz = 40, MaxHz = 6000;
    public const double Threshold = 0.15, Clear = 0.35;

    /// <summary>Decodes a BRR sample the way the S-DSP does (DSP_SFC.DecodeBrr: the same shifts, filters, clamps and the doubled 15-bit output), following its loop when it has one, for <paramref name="samples"/> output samples (fewer for a sample that ends).</summary>
    public static short[] Decode(byte[] brr, int loopOffset, int samples, out bool loops)
    {
        loops = false;
        var o = new List<short>(samples);
        if (brr.Length < 9) return [];
        int p1 = 0, p2 = 0;           // the previous two output samples, as the DSP keeps them (already doubled)
        int pos = 0;
        int guard = 0;
        while (o.Count < samples && guard++ < 100000)
        {
            if (pos + 9 > brr.Length) break;
            int header = brr[pos];
            int shift = header >> 4, filter = header & 0x0C;
            for (int k = 0; k < 8; k++)
            {
                int bytev = brr[pos + 1 + k];
                for (int h = 0; h < 2; h++)
                {
                    int nib = h == 0 ? bytev >> 4 : bytev & 15;
                    int s = (nib ^ 8) - 8;                     // sign-extended nybble
                    s = (s << shift) >> 1;
                    if (shift >= 0x0D) s = (s >> 25) << 11;
                    int q1 = p1, q2 = p2 >> 1;
                    if (filter >= 8)
                    {
                        s += q1 - q2;
                        if (filter == 8) { s += q2 >> 4; s += (q1 * -3) >> 6; }
                        else { s += (q1 * -13) >> 7; s += (q2 * 3) >> 4; }
                    }
                    else if (filter != 0) { s += q1 >> 1; s += (-q1) >> 5; }
                    s = (short)(Math.Clamp(s, -32768, 32767) * 2);
                    p2 = p1; p1 = s;
                    o.Add((short)s);
                }
            }
            if ((header & 1) != 0)
            {
                if ((header & 2) == 0) break;
                loops = true;
                if (loopOffset < 0 || loopOffset + 9 > brr.Length || loopOffset % 9 != 0) break;      // a loop that leaves the sample: what it plays is not this sample's
                pos = loopOffset;
            }
            else pos += 9;
        }
        if (o.Count > samples) o.RemoveRange(samples, o.Count - samples);
        return o.ToArray();
    }

    /// <summary>The fundamental of a BRR sample at pitch $1000. <paramref name="loopOffset"/> is the loop point's offset in the sample's bytes (-1 none).</summary>
    public static RootEstimate Estimate(byte[] brr, int loopOffset)
    {
        int maxLag = (int)(Rate / MinHz) + 2;
        var pcm = Decode(brr, loopOffset, 16384, out bool loops);
        if (pcm.Length < 2 * maxLag / 3 + 64) return new RootEstimate(0, 0, $"too short to have a pitch ({pcm.Length} samples)");
        // the part that is the instrument's steady sound: for a looping sample, the last stretch (the loop played over and over); for a one-shot, from a tenth in, past the attack
        int n = Math.Min(4096, pcm.Length);
        int from = loops ? pcm.Length - n : Math.Min(pcm.Length / 10, pcm.Length - n);
        double energy = 0;
        for (int i = from; i < from + n; i++) energy += (double)pcm[i] * pcm[i];
        if (energy / n < 4.0) return new RootEstimate(0, 0, "silent");
        int w = Math.Max(64, n - Math.Min(maxLag, n / 2));
        int lagMax = Math.Min(maxLag, n - w - 1);
        int lagMin = Math.Max(2, (int)(Rate / MaxHz));
        if (lagMax <= lagMin + 2) return new RootEstimate(0, 0, "too short to have a pitch");
        // the difference function at HALF-sample lags (the odd ones from the mean of two neighbours): a sample's period is rarely a whole number of samples, and a bright sound
        // (a saw) compared with itself one half sample off looks unlike itself, which would send the estimate an octave down to the first whole-sample repeat
        int jMax = 2 * lagMax, jMin = 2 * lagMin;
        var d = new double[jMax + 1];
        for (int j = 1; j <= jMax; j++)
        {
            int k = j >> 1;
            double sum = 0;
            if ((j & 1) == 0) for (int i = 0; i < w; i++) { double diff = pcm[from + i] - pcm[from + i + k]; sum += diff * diff; }
            else for (int i = 0; i < w; i++) { double diff = pcm[from + i] - 0.5 * (pcm[from + i + k] + pcm[from + i + k + 1]); sum += diff * diff; }
            d[j] = sum;
        }
        var dn = new double[jMax + 1];
        dn[0] = 1;
        double run = 0;
        for (int j = 1; j <= jMax; j++) { run += d[j]; dn[j] = run > 0 ? d[j] * j / run : 1; }
        double gmin = double.MaxValue;
        for (int j = jMin; j < jMax; j++) gmin = Math.Min(gmin, dn[j]);
        // the first dip that is nearly as deep as the deepest (a repeat of the period two or three periods on is as good a match: the first one is the period)
        double accept = Math.Max(Threshold, gmin + 0.2);
        int best = -1;
        for (int j = jMin; j < jMax; j++)
            if (dn[j] < accept)
            {
                while (j + 1 < jMax && dn[j + 1] < dn[j]) j++;
                best = j; break;
            }
        string how = loops ? "the loop" : "the one-shot";
        if (best < 0 || dn[best] >= Clear) return new RootEstimate(0, best < 0 ? 1 - gmin : 1 - dn[best], $"no clear pitch in {how} (aperiodicity {(best < 0 ? gmin : dn[best]):0.00})");
        double refined = best;
        if (best > jMin && best < jMax)
        {
            double a = dn[best - 1], b = dn[best], c = dn[best + 1], den = a - 2 * b + c;
            if (Math.Abs(den) > 1e-12) refined = best + Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5);
        }
        refined /= 2;
        return new RootEstimate(Rate / refined, 1 - dn[best], $"from {how}, period {refined:0.00} samples");
    }

    /// <summary>The fraction of a semitone the game keys an instrument's notes at, as a circular mean over the key-on pitch registers (in cents, -50..50), and how concentrated they are (1 = every note at the same fraction).
    /// <paramref name="tune"/> is the tuning (cents) the preset plays with: register $1000 sounds at MIDI 60 - tune/100.</summary>
    public static (double Cents, double Concentration) DriverOffset(IEnumerable<int> keyOnPitches, int tune)
    {
        double sx = 0, sy = 0; int n = 0;
        foreach (int p in keyOnPitches)
        {
            if (p <= 0) continue;
            double m = 60 + (1200 * Math.Log2(p / 4096.0) - tune) / 100.0;
            double frac = m - Math.Round(m);
            sx += Math.Cos(2 * Math.PI * frac); sy += Math.Sin(2 * Math.PI * frac); n++;
        }
        if (n == 0) return (0, 0);
        double angle = Math.Atan2(sy, sx), r = Math.Sqrt(sx * sx + sy * sy) / n;
        return (angle / (2 * Math.PI) * 100, r);
    }

    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>FL's name of a MIDI note (FL calls MIDI 60 "C5").</summary>
    public static string NoteName(int midi) => midi is < 0 or > 127 ? midi.ToString() : Names[midi % 12] + (midi / 12).ToString();

    public static string NoteName(double midi)
    {
        int k = (int)Math.Round(midi);
        int c = (int)Math.Round((midi - k) * 100);
        return NoteName(k) + (c == 0 ? "" : c > 0 ? $" +{c}" : $" {c}") + (c == 0 ? "" : " cents");
    }
}
