// TAS workline T1 (docs/projects/tas-format.md): the per-console button maps of the TAS script language.
// A map names a console's pad buttons in a fixed order (bit i of a pad's input word = Buttons[i]); the script, the per-frame log and the
// movie exporters all use that order. A new console (T7) adds one map here and one target in TasTargets.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BrokenNes.Workshop.Tas.Script;

public sealed class TasButtonMap
{
    /// <summary>The console key this map is for (nes, n64, ...).</summary>
    public string Console { get; }
    /// <summary>Canonical names, in bit order.</summary>
    public IReadOnlyList<string> Buttons { get; }
    /// <summary>Whether the pad has an analog stick (the script's <c>stick X Y</c>).</summary>
    public bool HasStick { get; }
    /// <summary>How many pads a script may drive.</summary>
    public int Pads { get; }
    private readonly Dictionary<string, int> _names = new(StringComparer.OrdinalIgnoreCase);

    public TasButtonMap(string console, string[] buttons, bool stick, int pads, params (string alias, string name)[] aliases)
    {
        Console = console; Buttons = buttons; HasStick = stick; Pads = pads;
        for (int i = 0; i < buttons.Length; i++) _names[buttons[i]] = i;
        foreach (var (a, n) in aliases) _names[a] = _names[n];
    }

    /// <summary>"A+B", "Right+B", "-" (none). Unknown names throw with the list of the map's buttons.</summary>
    public ulong Parse(string text, int line = 0)
    {
        ulong b = 0;
        foreach (var t in text.Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (t == "-" || t.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_names.TryGetValue(t, out int i)) throw new FormatException($"{(line > 0 ? $"line {line}: " : "")}unknown {Console} button '{t}' ({string.Join(" ", Buttons)})");
            b |= 1UL << i;
        }
        return b;
    }

    public string Text(ulong bits)
    {
        if (bits == 0) return "-";
        var l = new List<string>();
        for (int i = 0; i < Buttons.Count; i++) if ((bits & (1UL << i)) != 0) l.Add(Buttons[i]);
        return string.Join("+", l);
    }

    public static readonly TasButtonMap Nes = new("nes", new[] { "A", "B", "Select", "Start", "Up", "Down", "Left", "Right" }, false, 2,
        ("U", "Up"), ("D", "Down"), ("L", "Left"), ("R", "Right"), ("SEL", "Select"), ("T", "Start"), ("S", "Select"));

    /// <summary>The N64 compat script names (UAT/n64/compat/README_compat.md): A B Z START L R CU CD CL CR DU DD DL DR.</summary>
    public static readonly TasButtonMap N64 = new("n64", new[] { "A", "B", "Z", "START", "L", "R", "CU", "CD", "CL", "CR", "DU", "DD", "DL", "DR" }, true, 4);

    /// <summary>The console-neutral BrokenNes pad (PadButtons names), for the consoles that run through a plain IConsoleSession (T7 starts here).</summary>
    public static TasButtonMap Generic(string console) => new(console,
        new[] { "Up", "Down", "Left", "Right", "A", "B", "X", "Y", "L", "R", "Start", "Select", "C", "Z", "Mode" }, false, 2);

    public static TasButtonMap For(string console) => console.ToLowerInvariant() switch
    {
        "nes" => Nes,
        "n64" => N64,
        var c => Generic(c),
    };
}
