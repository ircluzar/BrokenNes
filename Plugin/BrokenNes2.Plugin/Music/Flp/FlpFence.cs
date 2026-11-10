// The music pipeline's "fence" (docs/projects/flp-music-pipeline.md section 8): every limit of a target engine, checked against the FL project and reported line by line, so nothing the game cannot
// play is dropped without the composer being told. One report per back end; the lines come from two places: the back end's own checks on the song model (before conversion) and the game's
// real converter's own figures (after it: the build scripts add them with the same rule ids). Pure: no UI, no files.
using System.Text;

namespace BrokenNes2.Music.Flp;

/// <summary>Ok = inside the limit (said so the composer sees the budget); Note = plays, but differently from FL; Over = a limit is broken: something in FL will not be in the game.</summary>
public enum FenceLevel { Ok, Note, Over }

public sealed record FenceLine(string Rule, FenceLevel Level, string Text);

public sealed class FenceReport
{
    public string Engine = "";
    public List<FenceLine> Lines = new();

    public FenceReport(string engine) { Engine = engine; }

    public void Ok(string rule, string text) => Lines.Add(new(rule, FenceLevel.Ok, text));
    public void Note(string rule, string text) => Lines.Add(new(rule, FenceLevel.Note, text));
    public void Over(string rule, string text) => Lines.Add(new(rule, FenceLevel.Over, text));

    public int Count(FenceLevel l) => Lines.Count(x => x.Level == l);
    public bool AnyOver => Lines.Any(x => x.Level == FenceLevel.Over);
    /// <summary>The caveats in phase 1's sense: everything that is not an Ok line.</summary>
    public IEnumerable<string> Caveats => Lines.Where(l => l.Level != FenceLevel.Ok).Select(l => (l.Level == FenceLevel.Over ? "OVER " : "") + l.Text);

    public string Summary => $"{Engine}: {Count(FenceLevel.Over)} over the limit, {Count(FenceLevel.Note)} played differently, {Count(FenceLevel.Ok)} inside";

    public string Text()
    {
        var sb = new StringBuilder();
        sb.Append(Summary).Append('\n');
        foreach (var lv in new[] { FenceLevel.Over, FenceLevel.Note, FenceLevel.Ok })
            foreach (var l in Lines.Where(x => x.Level == lv)) sb.Append(lv == FenceLevel.Over ? "OVER " : lv == FenceLevel.Note ? "note " : "ok   ").Append($"[{l.Rule}] {l.Text}\n");
        return sb.ToString();
    }

    /// <summary>JSON the build scripts read and extend (format brokennes-fence v1): {"format","version","engine","lines":[{"rule","level","text"}]}.</summary>
    public string Json()
    {
        var sb = new StringBuilder();
        sb.Append("{\"format\":\"brokennes-fence\",\"version\":1,\"engine\":").Append(Q(Engine)).Append(",\"lines\":[");
        for (int i = 0; i < Lines.Count; i++)
        {
            var l = Lines[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"rule\":").Append(Q(l.Rule)).Append(",\"level\":").Append(Q(l.Level.ToString().ToLowerInvariant())).Append(",\"text\":").Append(Q(l.Text)).Append('}');
        }
        return sb.Append("]}\n").ToString();
    }

    private static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 32 || c > 126) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
