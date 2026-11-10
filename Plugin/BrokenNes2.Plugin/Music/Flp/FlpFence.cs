// The "fence" of a game's sound engine: every limit the engine has (channels, instruments, duty, volume, length/bytes, note range, effects), checked against the song, one line per rule.
// A line says what fits, or what is out of bounds and by how much, and what the game does instead. Nothing a back end changes or leaves out is without a line.
namespace BrokenNes2.Music.Flp;

public enum Fit { Ok, Changed, Out }

/// <summary>One rule of an engine's fence: <see cref="Fit.Ok"/> the song is inside it, <see cref="Fit.Changed"/> the game plays it differently (said how), <see cref="Fit.Out"/> out of bounds (the game cannot play it as composed).</summary>
public sealed record FenceLine(string Rule, Fit Fit, string Text)
{
    public override string ToString() => $"{(Fit == Fit.Ok ? "ok " : Fit == Fit.Changed ? "CHANGED" : "OUT")} {Rule}: {Text}";
}

public static class FlpFence
{
    /// <summary>The fence as text lines, the problems first (Out, then Changed, then Ok).</summary>
    public static IEnumerable<string> Lines(IEnumerable<FenceLine> f) => f.OrderBy(l => l.Fit == Fit.Out ? 0 : l.Fit == Fit.Changed ? 1 : 2).Select(l => l.ToString());

    /// <summary>A short summary: "3 out of bounds, 2 changed, 9 ok".</summary>
    public static string Summary(IEnumerable<FenceLine> f)
    {
        var l = f.ToList();
        return $"{l.Count(x => x.Fit == Fit.Out)} out of bounds, {l.Count(x => x.Fit == Fit.Changed)} changed, {l.Count(x => x.Fit == Fit.Ok)} ok";
    }

    /// <summary>The MIDI note name of a key (C5 = 60, FL's convention).</summary>
    public static string NoteName(int key) => new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }[((key % 12) + 12) % 12] + (key / 12 - (key < 0 && key % 12 != 0 ? 1 : 0));
}
