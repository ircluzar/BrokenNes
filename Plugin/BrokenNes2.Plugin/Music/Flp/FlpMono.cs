// One BrokenNes2 instance as the plugin plays it in Direct mode (Bn2Plugin.ResolveOwner, Record/NesDirect.cs): one note at a time, the newest held note sounds; FL's slide notes glide the
// sounding note in FL's measured shape (VrunNesBackend.SlideEarly / SlideExtra); pitch in cents from MIDI note 0 = FL key x 100 + note fine pitch + Coarse/Fine; level = round(15 x velocity/128 x Volume).
// The same rules as VrunNesBackend's (which keeps its own copy: another agent owns that file); used by the back ends that are not VRUN NES (SuperVrunBackend, ...). Pure.
namespace BrokenNes2.Music.Flp;

public sealed class MonoGlide { public double Start, Len, To; public int At; public bool Hit; }

public sealed class MonoVoice
{
    public int Start, End, Order, Velocity;
    public double Base, Volume;
    public List<MonoGlide> Glides = new();

    public double PitchAt(double t)
    {
        double cur = Base;
        foreach (var g in Glides)
        {
            if (t <= g.Start) break;
            double from = cur;
            if (g.Len <= 0 || t >= g.Start + g.Len) cur = g.To;
            else { cur = from + (g.To - from) * (t - g.Start) / g.Len; break; }
        }
        return cur;
    }

    public int Level => Math.Clamp((int)Math.Round(15 * (Velocity / 128.0) * Volume, MidpointRounding.AwayFromZero), 0, 15);
}

/// <summary>A stretch of time the voice owns its instance (FL ticks).</summary>
public readonly record struct MonoSeg(MonoVoice V, int From, int To);

public sealed class MonoPart
{
    public List<MonoVoice> Voices = new();
    public List<MonoSeg> Segs = new();
    public int Empty, LoneSlides, Cut;
}

public static class FlpMono
{
    public static MonoPart Resolve(IEnumerable<FlpInstrument> group)
    {
        var part = new MonoPart();
        var glides = new List<MonoGlide>();
        int order = 0;
        foreach (var ins in group)
        {
            var b = ins.Bn2!;
            double tune = b.Param(3) * 100 + b.Param(4);
            double volume = Math.Clamp(b.Param(1, 1000) / 1000.0, 0, 1);
            foreach (var n in ins.Notes)
            {
                double cents = n.Key * 100 + (n.Fine - 120) + tune;
                if (n.Slide) glides.Add(new MonoGlide { At = n.Start, Start = n.Start - VrunNesBackend.SlideEarly, Len = Math.Max(0, n.End - n.Start) + VrunNesBackend.SlideExtra, To = cents });
                else if (n.End > n.Start) part.Voices.Add(new MonoVoice { Start = n.Start, End = n.End, Base = cents, Velocity = n.Velocity, Volume = volume, Order = order++ });
                else part.Empty++;
            }
        }
        foreach (var g in glides)
        {
            foreach (var v in part.Voices) if (v.Start <= g.At && g.At < v.End) { v.Glides.Add(g); g.Hit = true; }
            if (!g.Hit) part.LoneSlides++;
        }
        foreach (var v in part.Voices) v.Glides.Sort((a, c) => a.Start.CompareTo(c.Start));
        var times = part.Voices.SelectMany(v => new[] { v.Start, v.End }).Distinct().OrderBy(x => x).ToList();
        for (int i = 0; i + 1 < times.Count; i++)
        {
            int t = times[i];
            MonoVoice? own = null;
            foreach (var v in part.Voices) if (v.Start <= t && t < v.End && (own == null || v.Start > own.Start || v.Start == own.Start && v.Order > own.Order)) own = v;
            if (own == null) continue;
            if (part.Segs.Count > 0 && part.Segs[^1].V == own && part.Segs[^1].To == t) part.Segs[^1] = new MonoSeg(own, part.Segs[^1].From, times[i + 1]);
            else part.Segs.Add(new MonoSeg(own, t, times[i + 1]));
        }
        part.Cut = part.Voices.Count(v => !part.Segs.Any(s => s.V == v && s.To == v.End));
        return part;
    }

    /// <summary>The most parts sounding at once (and the first tick where that happens) over a set of segment lists.</summary>
    public static (int Max, int At) Peak(IEnumerable<IEnumerable<MonoSeg>> parts)
    {
        var ev = new List<(int T, int D)>();
        foreach (var p in parts) foreach (var s in p) if (s.V.Level > 0) { ev.Add((s.From, 1)); ev.Add((s.To, -1)); }
        ev.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.D.CompareTo(b.D));
        int cur = 0, max = 0, at = 0;
        foreach (var e in ev) { cur += e.D; if (cur > max) { max = cur; at = e.T; } }
        return (max, at);
    }

    /// <summary>Ticks during which more than <paramref name="limit"/> parts sound (summed).</summary>
    public static int TicksOver(IEnumerable<IEnumerable<MonoSeg>> parts, int limit)
    {
        var ev = new List<(int T, int D)>();
        foreach (var p in parts) foreach (var s in p) if (s.V.Level > 0) { ev.Add((s.From, 1)); ev.Add((s.To, -1)); }
        ev.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.D.CompareTo(b.D));
        int cur = 0, last = 0, over = 0;
        foreach (var e in ev) { if (cur > limit) over += e.T - last; cur += e.D; last = e.T; }
        return over;
    }
}
