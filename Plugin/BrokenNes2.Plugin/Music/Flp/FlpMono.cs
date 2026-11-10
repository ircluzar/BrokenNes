// One channel of a console as the plugin plays it from FL's notes: the instances that sit on it, one note at a time (the newest held note owns the channel: Bn2Plugin.ResolveOwner),
// pitch = FL key + note fine pitch + Coarse/Fine (cents from MIDI note 0), FL's slide notes glide the sounding note (FL's measured slide shape: VrunNesBackend.SlideEarly / SlideExtra),
// level = round(15 x velocity/128 x Volume). The same rules as VrunNesBackend (phase 1), shared here by the back ends of the other consoles (Vaults GB / GG, CTW SMS) so that file is not touched.
namespace BrokenNes2.Music.Flp;

public sealed class FlpGlide { public double Start, Len, To; public int At; public bool Hit; }

public sealed class FlpVoice
{
    public int Start, End, Order, Velocity;
    public double Base, Volume;
    public FlpInstrument Instrument = null!;
    public List<FlpGlide> Glides = new();

    /// <summary>The voice's pitch (cents from MIDI note 0) at an FL tick, its glides applied.</summary>
    public double PitchAt(double t)
    {
        double cur = Base;
        foreach (var g in Glides)
        {
            if (t <= g.Start) break;
            if (g.Len <= 0 || t >= g.Start + g.Len) cur = g.To;
            else { cur += (g.To - cur) * (t - g.Start) / g.Len; break; }
        }
        return cur;
    }

    /// <summary>The level the plugin plays (0-15).</summary>
    public int Level => Math.Clamp((int)Math.Round(15 * (Velocity / 128.0) * Volume, MidpointRounding.AwayFromZero), 0, 15);
}

/// <summary>A stretch of FL ticks one voice owns the channel.</summary>
public readonly record struct FlpSegment(FlpVoice Voice, int From, int To);

public sealed class FlpMonoChannel
{
    public List<FlpVoice> Voices = new();
    public List<FlpSegment> Segments = new();
    /// <summary>Notes of length 0, slides with no note under them, notes that never own the channel (a newer one covers them whole), notes cut short by a newer one.</summary>
    public int Empty, LoneSlides, Hidden, Cut;

    public static FlpMonoChannel Of(IEnumerable<FlpInstrument> instruments)
    {
        var m = new FlpMonoChannel();
        var glides = new List<FlpGlide>();
        int order = 0;
        foreach (var ins in instruments)
        {
            var b = ins.Bn2!;
            double tune = b.Param(3) * 100 + b.Param(4);
            double volume = Math.Clamp(b.Param(1, 1000) / 1000.0, 0, 1);
            foreach (var n in ins.Notes)
            {
                double cents = n.Key * 100 + n.Fine - 120 + tune;
                if (n.Slide) glides.Add(new FlpGlide { At = n.Start, Start = n.Start - VrunNesBackend.SlideEarly, Len = Math.Max(0, n.End - n.Start) + VrunNesBackend.SlideExtra, To = cents });
                else if (n.End > n.Start) m.Voices.Add(new FlpVoice { Start = n.Start, End = n.End, Base = cents, Velocity = n.Velocity, Volume = volume, Order = order++, Instrument = ins });
                else m.Empty++;
            }
        }
        foreach (var g in glides)
        {
            foreach (var v in m.Voices) if (v.Start <= g.At && g.At < v.End) { v.Glides.Add(g); g.Hit = true; }
            if (!g.Hit) m.LoneSlides++;
        }
        foreach (var v in m.Voices) v.Glides.Sort((a, c) => a.Start.CompareTo(c.Start));
        var times = m.Voices.SelectMany(v => new[] { v.Start, v.End }).Distinct().OrderBy(x => x).ToList();
        for (int i = 0; i + 1 < times.Count; i++)
        {
            int t = times[i];
            FlpVoice? own = null;
            foreach (var v in m.Voices) if (v.Start <= t && t < v.End && (own == null || v.Start > own.Start || v.Start == own.Start && v.Order > own.Order)) own = v;
            if (own == null) continue;
            if (m.Segments.Count > 0 && m.Segments[^1].Voice == own && m.Segments[^1].To == t) m.Segments[^1] = new FlpSegment(own, m.Segments[^1].From, times[i + 1]);
            else m.Segments.Add(new FlpSegment(own, t, times[i + 1]));
        }
        var owned = m.Segments.Select(s => s.Voice).ToHashSet();
        m.Hidden = m.Voices.Count(v => !owned.Contains(v));
        m.Cut = m.Voices.Count(v => owned.Contains(v) && m.Segments.Where(s => s.Voice == v).Max(s => s.To) < v.End);
        return m;
    }
}
