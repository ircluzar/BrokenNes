using System.Text;

namespace BrokenNes.FruityHost;

public sealed record CertRow(string Label, string Kind, string Measured, string Expected, string Error, string Verdict);

public sealed class CertReport
{
    public List<CertRow> Rows { get; } = new();
    public bool Failed => Rows.Any(r => r.Verdict == "FAIL");
    public string Text { get; set; } = "";
}

/// <summary>
/// Checks audio against a fixture (a <see cref="Scenario"/>): did every note sound at the pitch the NES chip should
/// produce for it, did slides and bends move as drawn, do louder notes come out louder, is everything between the
/// notes silent. Works on the test host's own output and, the point of it, on audio rendered by FL Studio.
/// </summary>
public static class Certify
{
    /// <summary>Times (ms, absolute) at which a pulse note's period crosses a multiple of 256, where BrokenNes2 must
    /// write $4003 and the NES pulse channel restarts its waveform (authentic hardware behaviour: a click, and a
    /// shortened or lengthened cycle that the pitch tracker reports as a spike). Triangles do not restart.</summary>
    public static List<double> RestartTimes(SimNote n, Scenario s, double startMs, double lenMs, bool triangle, double coarseCents = 0)
    {
        var times = new List<double>();
        if (triangle) return times;
        int prevHi = -1;
        for (double ms = 0; ms <= lenMs; ms += 1)
        {
            int p = Nes.Period(Scenario.PitchCents(n, ms / s.MsPerBeat), false, (int)coarseCents);
            int hi = (p >> 8) & 7;
            if (prevHi >= 0 && hi != prevHi) times.Add(startMs + ms);
            prevHi = hi;
        }
        return times;
    }

    /// <summary>Pitch error of a measured track against a note's ideal curve: (rms, max, windows) in cents. Windows within
    /// <paramref name="skipMs"/> of a waveform restart are left out (see <see cref="RestartTimes"/>) and counted.</summary>
    public static (double Rms, double Max, int Windows) TrackError(float[] mono, int rate, double startMs, double lenMs, SimNote n, Scenario s,
        double fromFrac, double toFrac, double latencyMs, IReadOnlyList<double>? restarts = null, double skipMs = 50)
    {
        var track = Audio.Track(mono, rate, startMs + lenMs * fromFrac, startMs + lenMs * toFrac, 40, 10);
        double sum = 0, max = 0; int count = 0;
        foreach (var (ms, hz) in track)
        {
            if (double.IsNaN(hz)) continue;
            if (restarts != null && restarts.Any(r => Math.Abs(ms - r) < skipMs)) continue;
            double beat = (ms - latencyMs - startMs) / s.MsPerBeat;
            double e = Audio.Cents(hz, Nes.KeyHz(60 + Scenario.PitchCents(n, beat) / 100.0));
            sum += e * e; max = Math.Max(max, Math.Abs(e)); count++;
        }
        return (count == 0 ? double.NaN : Math.Sqrt(sum / count), max, count);
    }

    public static CertReport Analyze(float[] mono, int rate, Scenario s, double offsetMs = 0)
    {
        var rep = new CertReport();
        var sb = new StringBuilder();
        double Ms(double beats) => beats * s.MsPerBeat + offsetMs;
        int Sample(double ms) => (int)(ms * rate / 1000);
        var rmsByLabel = new Dictionary<string, double>();

        foreach (var n in s.Notes)
        {
            string label = n.Label ?? $"key{n.Key}";
            double startMs = Ms(n.StartBeat), lenMs = n.LengthBeats * s.MsPerBeat;
            int ch = n.Color & 3;
            bool triangle = ch == 2, noise = ch == 3;
            int a = Sample(startMs + 150), b = Sample(startMs + lenMs - 100);
            double rms = Audio.Rms(mono, Sample(startMs + 100), Sample(startMs + lenMs - 60));
            rmsByLabel[label] = rms;

            if (rms < 0.003)
            {
                rep.Rows.Add(new CertRow(label, "sound", "silent", "audible", "-", "FAIL"));
                continue;
            }

            if (noise)
            {
                rep.Rows.Add(new CertRow(label, "noise", $"rms {rms:0.000}", "audible", "-", "PASS"));
            }
            else if (n.Slides is { Count: > 0 } || n.Bend is { Count: > 0 })
            {
                bool slide = n.Slides is { Count: > 0 };
                // FL's tick rate and ours differ: find the timing offset that fits best (within +-60 ms) and report it
                var restarts = RestartTimes(n, s, startMs, lenMs, triangle);
                double bestLat = 0, bestRms = double.MaxValue; double bestMax = 0; int bestN = 0;
                for (double lat = -60; lat <= 60; lat += 2)
                {
                    var (r, m, c) = TrackError(mono, rate, startMs, lenMs, n, s, 0.1, 0.9, lat, restarts);
                    if (c > 20 && r < bestRms) { bestRms = r; bestMax = m; bestN = c; bestLat = lat; }
                }
                double limitRms = slide ? 12 : 10, limitMax = slide ? 35 : 28;
                bool ok = bestN > 20 && bestRms < limitRms && bestMax < limitMax;
                rep.Rows.Add(new CertRow(label, slide ? "slide" : "bend", $"rms {bestRms:0.0} / max {bestMax:0.0} cents over {bestN} windows", $"rms < {limitRms}, max < {limitMax}",
                    $"best timing offset {bestLat:+0;-0;0} ms" + (restarts.Count > 0 ? $"; {restarts.Count} waveform restart(s) excluded" : ""), ok ? "PASS" : "FAIL"));
            }
            else
            {
                double hz = Audio.Frequency(mono, rate, a, b);
                int period = Nes.Period((n.Key - 60) * 100.0, triangle);
                double want = Nes.PeriodHz(period, triangle);
                double err = double.IsNaN(hz) ? double.NaN : Audio.Cents(hz, want);
                bool ok = !double.IsNaN(err) && Math.Abs(err) <= 3;
                rep.Rows.Add(new CertRow(label, triangle ? "triangle pitch" : "pulse pitch", $"{hz:0.00} Hz", $"{want:0.00} Hz (period {period})", $"{err:+0.0;-0.0} cents", ok ? "PASS" : "FAIL"));
            }
        }

        // loudness must follow velocity
        var vel = s.Notes.Where(x => x.Label?.StartsWith("vel-") == true).OrderByDescending(x => x.Velocity).ToList();
        if (vel.Count >= 2)
        {
            var seq = vel.Select(x => rmsByLabel[x.Label!]).ToList();
            bool mono_ = Enumerable.Range(1, seq.Count - 1).All(i => seq[i] < seq[i - 1]);
            rep.Rows.Add(new CertRow("velocity", "loudness", string.Join(" > ", seq.Select(v => v.ToString("0.000"))), "falling with velocity", "-", mono_ ? "PASS" : "FAIL"));
        }

        // silence between and after the notes: nothing may be stuck
        var ends = s.Notes.Select(x => Ms(x.StartBeat + x.LengthBeats)).OrderBy(x => x).ToList();
        var starts = s.Notes.Select(x => Ms(x.StartBeat)).OrderBy(x => x).ToList();
        int stuck = 0; double worst = 0;
        for (int i = 0; i < ends.Count; i++)
        {
            double gapStart = ends[i] + 80, gapEnd = i + 1 < starts.Count ? starts[i + 1] - 20 : ends[i] + 400;
            if (gapEnd - gapStart < 40) continue;
            double r = Audio.Rms(mono, Sample(gapStart), Sample(gapEnd));
            if (r > 0.002) { stuck++; worst = Math.Max(worst, r); }
        }
        rep.Rows.Add(new CertRow("silence", "gaps", stuck == 0 ? "all gaps silent" : $"{stuck} gaps not silent (worst rms {worst:0.000})", "silent", "-", stuck == 0 ? "PASS" : "FAIL"));

        sb.AppendLine($"{"note",-22} {"kind",-15} {"measured",-52} {"expected",-30} {"error",-30} result");
        foreach (var r in rep.Rows) sb.AppendLine($"{r.Label,-22} {r.Kind,-15} {r.Measured,-52} {r.Expected,-30} {r.Error,-30} {r.Verdict}");
        int fail = rep.Rows.Count(r => r.Verdict == "FAIL");
        sb.AppendLine($"{rep.Rows.Count - fail} of {rep.Rows.Count} checks passed" + (fail > 0 ? $", {fail} FAILED" : ""));
        rep.Text = sb.ToString();
        return rep;
    }
}
