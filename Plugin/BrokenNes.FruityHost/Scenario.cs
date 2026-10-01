// A timeline of piano-roll notes, in the terms FL uses (beats, MIDI keys with C5 = 60, note colour,
// velocity), used three ways: the host mixer plays it into the plugin, the FL fixture generator
// writes it into a project, and the analyser derives what the audio should contain from it.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrokenNes.FruityHost;

public sealed class SimNote
{
    public double StartBeat { get; set; }
    public double LengthBeats { get; set; }
    /// <summary>MIDI key; FL's C5 = 60, so key 69 is 440 Hz.</summary>
    public double Key { get; set; } = 60;
    /// <summary>Piano-roll note colour 0-15 (BrokenNes2 maps it to an NES channel).</summary>
    public int Color { get; set; }
    public double Velocity { get; set; } = 0.78;
    public double Pan { get; set; }
    /// <summary>Slide notes chained onto this note, in order. In FL each is a slide-flagged note: the pitch glides
    /// linearly from wherever it is to <see cref="SlideSeg.ToKey"/>, starting <see cref="SlideSeg.StartBeat"/> beats
    /// after this note began and taking <see cref="SlideSeg.LengthBeats"/>. A slid note ends with its last slide
    /// (<see cref="LengthBeats"/> must equal that end), as FL's does.</summary>
    public List<SlideSeg>? Slides { get; set; }
    /// <summary>Per-note pitch automation: (beat after note start, cents) points, linear in between. A host-test aid:
    /// FL's piano roll cannot draw this, so it is not in FL fixtures.</summary>
    public List<double[]>? Bend { get; set; }
    /// <summary>What the analyser expects of this note, if anything (see <see cref="Expectation"/>).</summary>
    public string? Label { get; set; }
}

public sealed class SlideSeg
{
    public double StartBeat { get; set; }
    public double LengthBeats { get; set; }
    public double ToKey { get; set; }
}

public sealed class Scenario
{
    public string Name { get; set; } = "";
    public double Tempo { get; set; } = 120;
    public double LengthBeats { get; set; }
    public List<SimNote> Notes { get; set; } = new();
    /// <summary>Plugin parameter overrides applied before playing (index -> value).</summary>
    public Dictionary<int, int> Params { get; set; } = new();

    [JsonIgnore] public double MsPerBeat => 60000.0 / Tempo;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static Scenario Load(string path) => JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Json)!;
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    /// <summary>Pitch of a note at <paramref name="beat"/> (beats after its start), in cents relative to C5 - what FL puts in FinalLevels.Pitch.</summary>
    public static double PitchCents(SimNote n, double beat)
    {
        double cents = (n.Key - 60) * 100;
        if (n.Slides is { Count: > 0 } slides)
        {
            double from = n.Key;
            foreach (var seg in slides)
            {
                if (beat < seg.StartBeat) break;
                double f = seg.LengthBeats <= 0 ? 1 : Math.Clamp((beat - seg.StartBeat) / seg.LengthBeats, 0, 1);
                cents = (from + (seg.ToKey - from) * f - 60) * 100;
                from = seg.ToKey;
            }
        }
        if (n.Bend is { Count: > 0 } pts)
        {
            if (beat <= pts[0][0]) cents += pts[0][1];
            else if (beat >= pts[^1][0]) cents += pts[^1][1];
            else
                for (int i = 1; i < pts.Count; i++)
                    if (beat <= pts[i][0])
                    {
                        double f = (beat - pts[i - 1][0]) / (pts[i][0] - pts[i - 1][0]);
                        cents += pts[i - 1][1] + (pts[i][1] - pts[i - 1][1]) * f;
                        break;
                    }
        }
        return cents;
    }
}

/// <summary>Built-in scenarios (also written as JSON for the FL fixture generator).</summary>
public static class Fixtures
{
    /// <summary>One note per channel, a slide, a bend, a volume ramp: the FL certification fixture. Each note
    /// sounds alone, so its pitch can be measured cleanly; the gaps are silence.</summary>
    public static Scenario FlCertify()
    {
        var s = new Scenario { Name = "fl-certify", Tempo = 120 };
        double t = 0;
        SimNote Add(double len, double key, int color, string label, Action<SimNote>? more = null)
        {
            var n = new SimNote { StartBeat = t, LengthBeats = len, Key = key, Color = color, Label = label };
            more?.Invoke(n);
            s.Notes.Add(n);
            t += len + 0.5;
            return n;
        }
        Add(1, 69, 0, "pulse1-A5");
        Add(1, 57, 0, "pulse1-A4");
        Add(1, 69, 1, "pulse2-A5");
        Add(1, 57, 2, "triangle-A4");
        Add(1, 69, 2, "triangle-A5");
        Add(1, 60, 3, "noise");
        Add(1.75, 60, 0, "slide-C5-to-C6", n => n.Slides = new() { new SlideSeg { StartBeat = 0.25, LengthBeats = 1.5, ToKey = 72 } });
        Add(1.75, 69, 1, "slide-down-A5-to-A4", n => n.Slides = new() { new SlideSeg { StartBeat = 0.25, LengthBeats = 1.5, ToKey = 57 } });
        Add(1.75, 57, 2, "slide-triangle-up", n => n.Slides = new() { new SlideSeg { StartBeat = 0.25, LengthBeats = 1.5, ToKey = 69 } });
        Add(2.25, 69, 0, "slide-chain-wobble", n => n.Slides = new()
        {
            new SlideSeg { StartBeat = 0.25, LengthBeats = 0.5, ToKey = 71 },
            new SlideSeg { StartBeat = 0.75, LengthBeats = 0.5, ToKey = 69 },
            new SlideSeg { StartBeat = 1.25, LengthBeats = 0.5, ToKey = 67 },
            new SlideSeg { StartBeat = 1.75, LengthBeats = 0.5, ToKey = 69 },
        });
        Add(1, 69, 0, "vel-high", n => n.Velocity = 1.0);
        Add(1, 69, 0, "vel-mid", n => n.Velocity = 0.5);
        Add(1, 69, 0, "vel-low", n => n.Velocity = 0.2);
        s.LengthBeats = t + 1;
        return s;
    }

    public static Scenario? ByName(string name) => name switch
    {
        "fl-certify" => FlCertify(),
        _ => null,
    };

    public static IEnumerable<string> Names => new[] { "fl-certify" };
}
