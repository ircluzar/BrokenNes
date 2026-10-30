using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NesEmulator.Debugging.Timeflow;

// =====================================================================================================================================
//  T4: search over the corpus (docs/projects/brokendev-timeflow-plan.md, "Search over the corpus" and "T4 as built").
//
//  A search condition is one or more clauses joined by "and", each optionally negated with "not". Every clause is about one frame:
//    exec <spec> [cpu C] [if <expr>]              a code breakpoint: an instruction at the address / range / symbol ran in the frame
//    read | write | access <spec> [cpu C] [if <expr>]   a memory breakpoint (all mirrors, DMA too; value, address, source in the expr)
//    state <expr>                                 a game-state expression on memory (and the timeline's watched variables) at the frame's end
//    finding [<text>]                             a D4 hardware-check finding first seen in the frame (its text contains <text>)
//    picture changed | picture still              the frame's picture differs from (equals) the frame before's
//    color #RRGGBB [in X Y W H] [min N] [tol T]   at least N pixels (default 1) of that colour (each channel within T) in the picture
//    sound silent [below L] | sound above L       the frame's sound peak (0..1) below L (default 0.01) / above L
//    channel <name> silent | playing              a sound channel (D8's monitor: "Pulse 1", "Voice 3", "Noise") at the frame's end
//  A hit is a frame where every clause holds; when a positive breakpoint clause is in it (the anchor), each of its firings in such a frame
//  is a hit (with the instruction, the cycle, the access).
// =====================================================================================================================================

public enum SearchClauseKind { Exec, Read, Write, Access, State, Finding, PictureChanged, PictureStill, Color, Sound, Channel }

/// <summary>A clause's answer for a frame in the coarse phase: certainly not, needs the exact phase, certainly.</summary>
public enum SearchTri : byte { No = 0, Maybe = 1, Yes = 2 }

/// <summary>One clause of a search condition.</summary>
public sealed class SearchClause
{
    public SearchClauseKind Kind;
    public bool Not;
    public string Text = "";
    /// <summary>A breakpoint's address, range or symbol, its CPU, its condition.</summary>
    public string Spec = "", CpuName = "main";
    public string? If;
    /// <summary>state: the expression.</summary>
    public string Expr = "";
    /// <summary>finding: the text a finding must contain ("" any).</summary>
    public string Contains = "";
    /// <summary>color: the colour (0xRRGGBB), the region (whole picture when W = 0), the pixels needed, the tolerance per channel.</summary>
    public uint Rgb; public int X, Y, W, H; public int Min = 1; public int Tol;
    /// <summary>sound: below (silent) or above, the peak level.</summary>
    public bool Above; public double Level = 0.01;
    /// <summary>channel: its name, and whether it must be playing (else silent).</summary>
    public string Channel = ""; public bool Playing;

    public bool IsBreakpoint => Kind is SearchClauseKind.Exec or SearchClauseKind.Read or SearchClauseKind.Write or SearchClauseKind.Access;
    public AccessKind Access => Kind switch { SearchClauseKind.Exec => AccessKind.Exec, SearchClauseKind.Read => AccessKind.Read, SearchClauseKind.Write => AccessKind.Write, _ => AccessKind.ReadWrite };
    /// <summary>What the clause is, by family (for the per-kind measurements): code, memory, state, finding, picture, sound.</summary>
    public string Family => Kind switch
    {
        SearchClauseKind.Exec => "code",
        SearchClauseKind.Read or SearchClauseKind.Write or SearchClauseKind.Access => "memory",
        SearchClauseKind.State => "state",
        SearchClauseKind.Finding => "finding",
        SearchClauseKind.PictureChanged or SearchClauseKind.PictureStill or SearchClauseKind.Color => "picture",
        _ => "sound",
    };
    /// <summary>The clause needs the debugger (code and memory breakpoints, the findings, the sound channel monitor).</summary>
    public bool NeedsDebugger => IsBreakpoint || Kind is SearchClauseKind.Channel;
    public override string ToString() => Text;
}

/// <summary>A search condition: its clauses (see the file's comment for the language).</summary>
public sealed class SearchCondition
{
    public string Text { get; private init; } = "";
    public IReadOnlyList<SearchClause> Clauses { get; private init; } = Array.Empty<SearchClause>();
    /// <summary>The first positive breakpoint clause: its firings are the hits.</summary>
    public SearchClause? Anchor => Clauses.FirstOrDefault(c => c.IsBreakpoint && !c.Not);

    public static readonly string[] Kinds = { "exec", "read", "write", "access", "state", "finding", "picture changed|still", "color", "sound silent|above", "channel" };

    public static string Help =>
        "clauses joined by 'and', each may start with 'not': exec <spec> [cpu C] [if <expr>]; read|write|access <spec> [cpu C] [if <expr>]; state <expr>; " +
        "finding [<text>]; picture changed|still; color #RRGGBB [in X Y W H] [min N] [tol T]; sound silent [below L] | sound above L; channel <name> silent|playing";

    /// <summary>Reads a condition; throws <see cref="FormatException"/> saying what is wrong.</summary>
    public static SearchCondition Parse(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new FormatException("an empty search condition (" + Help + ")");
        var parts = Regex.Split(text, @"\s+and\s+", RegexOptions.IgnoreCase);
        var list = new List<SearchClause>();
        foreach (var raw in parts) list.Add(ParseClause(raw.Trim(), text));
        return new SearchCondition { Text = text, Clauses = list };
    }

    private static SearchClause ParseClause(string s, string whole)
    {
        var c = new SearchClause { Text = s };
        if (s.Length == 0) throw new FormatException($"an empty clause in '{whole}' ('and' twice, or at an end)");
        if (Regex.IsMatch(s, @"^not\s+", RegexOptions.IgnoreCase)) { c.Not = true; s = Regex.Replace(s, @"^not\s+", "", RegexOptions.IgnoreCase).Trim(); }
        var t = Regex.Matches(s, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
        if (t.Count == 0) throw new FormatException($"an empty clause after 'not' in '{whole}'");
        string verb = t[0].ToLowerInvariant();
        string Rest(int from) => string.Join(" ", t.Skip(from));
        switch (verb)
        {
            case "exec" or "code" or "break" or "read" or "write" or "access":
            {
                c.Kind = verb switch { "read" => SearchClauseKind.Read, "write" => SearchClauseKind.Write, "access" => SearchClauseKind.Access, _ => SearchClauseKind.Exec };
                if (t.Count < 2) throw new FormatException($"'{verb}' needs an address, range or symbol ('{verb} $8000', '{verb} player_hp', '{verb} $0200-$02FF')");
                c.Spec = t[1];
                int i = 2;
                while (i < t.Count)
                {
                    string k = t[i].ToLowerInvariant();
                    if (k == "cpu" && i + 1 < t.Count) { c.CpuName = t[i + 1]; i += 2; continue; }
                    if (k == "if") { c.If = Rest(i + 1).Trim(); if (c.If.Length == 0) throw new FormatException($"'{s}': 'if' needs an expression"); i = t.Count; break; }
                    throw new FormatException($"'{s}': '{t[i]}' is not understood here (after the address: cpu <id>, if <expression>)");
                }
                // the expression's own text, not the tokens re-joined (quotes and spaces as written)
                if (c.If != null) { int at = Regex.Match(s, @"\sif\s", RegexOptions.IgnoreCase).Index; if (at > 0) c.If = s[(at + 4)..].Trim(); }
                return c;
            }
            case "state":
                c.Kind = SearchClauseKind.State;
                c.Expr = s.Length > 5 ? s[5..].Trim() : "";
                if (c.Expr.Length == 0) throw new FormatException("'state' needs an expression ('state [enemy_hp] > 0 && [enemy_x] < 240')");
                return c;
            case "finding" or "findings":
                c.Kind = SearchClauseKind.Finding;
                c.Contains = Rest(1).Trim();
                return c;
            case "picture" or "screen":
            {
                string w = t.Count > 1 ? t[1].ToLowerInvariant() : "";
                c.Kind = w switch
                {
                    "changed" or "changes" => SearchClauseKind.PictureChanged,
                    "still" or "same" or "unchanged" => SearchClauseKind.PictureStill,
                    _ => throw new FormatException($"'{s}': picture changed or picture still (a colour: color #RRGGBB ...)"),
                };
                if (t.Count > 2) throw new FormatException($"'{s}': nothing goes after picture {w}");
                return c;
            }
            case "color" or "colour":
            {
                c.Kind = SearchClauseKind.Color;
                if (t.Count < 2) throw new FormatException("'color' needs a colour (#F83800, $F83800 or 0xF83800)");
                c.Rgb = (uint)ParseRgb(t[1]);
                int i = 2;
                while (i < t.Count)
                {
                    string k = t[i].ToLowerInvariant();
                    if (k == "in" && i + 4 < t.Count) { c.X = Int(t[i + 1]); c.Y = Int(t[i + 2]); c.W = Int(t[i + 3]); c.H = Int(t[i + 4]); if (c.W <= 0 || c.H <= 0) throw new FormatException($"'{s}': the region's width and height must be above 0"); i += 5; continue; }
                    if (k == "min" && i + 1 < t.Count) { c.Min = Math.Max(1, Int(t[i + 1])); i += 2; continue; }
                    if (k == "tol" && i + 1 < t.Count) { c.Tol = Math.Clamp(Int(t[i + 1]), 0, 255); i += 2; continue; }
                    throw new FormatException($"'{s}': '{t[i]}' is not understood (after the colour: in X Y W H, min N, tol T)");
                }
                return c;
            }
            case "sound" or "audio":
            {
                c.Kind = SearchClauseKind.Sound;
                string w = t.Count > 1 ? t[1].ToLowerInvariant() : "";
                if (w is "silent" or "quiet") { c.Above = false; if (t.Count >= 4 && t[2].Equals("below", StringComparison.OrdinalIgnoreCase)) c.Level = Dbl(t[3]); else if (t.Count > 2) throw new FormatException($"'{s}': sound silent [below L]"); }
                else if (w is "above" or "louder" && t.Count == 3) { c.Above = true; c.Level = Dbl(t[2]); }
                else if (w is "below" && t.Count == 3) { c.Above = false; c.Level = Dbl(t[2]); }
                else throw new FormatException($"'{s}': sound silent [below L], sound below L or sound above L (L the frame's peak, 0 to 1)");
                if (c.Level is < 0 or > 1) throw new FormatException($"'{s}': the level is a peak from 0 to 1");
                return c;
            }
            case "channel" or "voice":
            {
                c.Kind = SearchClauseKind.Channel;
                if (t.Count < 3) throw new FormatException("'channel' needs a name and silent or playing ('channel \"Pulse 1\" silent', 'channel noise playing')");
                string last = t[^1].ToLowerInvariant();
                c.Playing = last switch { "playing" or "on" => true, "silent" or "off" => false, _ => throw new FormatException($"'{s}': the channel's name, then silent or playing") };
                c.Channel = string.Join(" ", t.Skip(1).Take(t.Count - 2));
                if (verb == "voice" && !c.Channel.StartsWith("voice", StringComparison.OrdinalIgnoreCase)) c.Channel = "Voice " + c.Channel;
                return c;
            }
            default:
                throw new FormatException($"unknown clause '{t[0]}' in '{whole}' ({Help})");
        }
    }

    private static int Int(string v) => (int)ProfileQuery.Num(v);
    private static double Dbl(string v) => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static int ParseRgb(string v)
    {
        string h = v.TrimStart('#', '$');
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int x)) throw new FormatException($"'{v}' is not a colour (#RRGGBB)");
        return x;
    }
}
