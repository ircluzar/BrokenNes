// TAS workline T1 (docs/projects/tas-format.md): the console-neutral TAS script language and its exact expansion.
// Grown from the N64 compat language (UAT/n64/compat/README_compat.md): every compat script is a TAS script, unchanged.
// Expansion turns a script into a flat program: one Frame step per emulated frame (every pad's buttons + stick, explicit),
// and Op steps between frames (checkpoint, screenshot, savestate, loadstate, reset, poke, peek, ...). pokehold / pokeeach /
// mirror / press / pulse / seconds are all resolved here, so the runner and the per-frame log know only frames and ops.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BrokenNes.Workshop.Tas.Script;

public struct TasPad : IEquatable<TasPad>
{
    public ulong Buttons; public sbyte X, Y;
    public TasPad(ulong b, sbyte x, sbyte y) { Buttons = b; X = x; Y = y; }
    public bool Equals(TasPad o) => Buttons == o.Buttons && X == o.X && Y == o.Y;
    public override bool Equals(object? o) => o is TasPad p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(Buttons, X, Y);
}

public sealed class TasStep
{
    /// <summary>null: a frame (Pads holds every pad's input for it). Otherwise an op run between frames.</summary>
    public string? Op;
    public string[] Args = Array.Empty<string>();
    public TasPad[] Pads = Array.Empty<TasPad>();
    public int Line;
    public bool IsFrame => Op == null;
}

public sealed class TasProgram
{
    public string Console = "";
    public TasButtonMap Map = TasButtonMap.Nes;
    public double Fps = 60;
    public readonly List<TasStep> Steps = new();
    public int Frames => Steps.Count(s => s.IsFrame);
    public int PadsUsed = 1;
}

public static class TasScript
{
    /// <summary>Ops the runner executes (between frames). Console-specific ones are checked by the target.</summary>
    public static readonly HashSet<string> RunnerOps = new(StringComparer.OrdinalIgnoreCase)
        { "checkpoint", "screenshot", "savestate", "loadstate", "reset", "poke", "peek", "savefile", "mark" };

    public static TasProgram ExpandFile(string path, TasButtonMap map, double fps, Func<string, bool>? consoleOp = null) =>
        Expand(File.ReadAllLines(path), map, fps, consoleOp);

    /// <param name="consoleOp">accepts console-only ops (N64: pak, prenmi, reload, clearsave, dumpram); null = none.</param>
    public static TasProgram Expand(IEnumerable<string> lines, TasButtonMap map, double fps, Func<string, bool>? consoleOp = null)
    {
        var p = new TasProgram { Console = map.Console, Map = map, Fps = fps };
        int pads = map.Pads;
        var held = new ulong[pads]; var sx = new sbyte[pads]; var sy = new sbyte[pads];
        int cur = 0, mirror = 1, maxPad = 0;
        var holds = new List<(string a, string h)>();
        int n = 0;

        void Frame(ulong extra)
        {
            foreach (var (a, h) in holds) p.Steps.Add(new TasStep { Op = "poke", Args = new[] { a, h }, Line = n });
            var f = new TasPad[pads];
            for (int i = 0; i < pads; i++) f[i] = new TasPad(held[i], sx[i], sy[i]);
            f[cur].Buttons |= extra;
            for (int m = 1; m < mirror; m++) f[m] = f[0];
            p.Steps.Add(new TasStep { Pads = f, Line = n });
        }

        foreach (var raw in lines)
        {
            n++;
            var l = raw; int c = l.IndexOf('#'); if (c >= 0) l = l[..c]; l = l.Trim(); if (l.Length == 0) continue;
            var t = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string op = t[0].ToLowerInvariant(); var a = t[1..];
            int I(int i, int d) => a.Length > i ? int.Parse(a[i], CultureInfo.InvariantCulture) : d;
            ulong B(int i) => a.Length > i ? map.Parse(a[i], n) : throw new FormatException($"line {n}: {op} needs buttons");
            switch (op)
            {
                case "run": case "frames": case "wait": { int k = I(0, 1); for (int i = 0; i < k; i++) Frame(0); break; }
                case "seconds": { int k = (int)Math.Round(double.Parse(a[0], CultureInfo.InvariantCulture) * fps); for (int i = 0; i < k; i++) Frame(0); break; }
                case "press": { var bt = B(0); int on = I(1, 5), off = I(2, 5); for (int i = 0; i < on; i++) Frame(bt); for (int i = 0; i < off; i++) Frame(0); break; }
                case "pulse": { var bt = B(0); int k = I(1, 60), per = I(2, 10); for (int i = 0; i < k; i++) Frame(i % per < per / 2 ? bt : 0); break; }
                case "hold": held[cur] |= B(0); break;
                case "release": if (a.Length == 0) held[cur] = 0; else held[cur] &= ~B(0); break;
                // input BTNS N: the held set becomes exactly BTNS, then N frames run (the compact form of a per-frame log; imports write it)
                case "input": { held[cur] = B(0); int k = I(1, 1); for (int i = 0; i < k; i++) Frame(0); break; }
                case "stick":
                    if (!map.HasStick) throw new FormatException($"line {n}: the {map.Console} pad has no analog stick");
                    sx[cur] = (sbyte)Math.Clamp(I(0, 0), -128, 127); sy[cur] = (sbyte)Math.Clamp(I(1, 0), -128, 127); break;
                case "pad": { int k = I(0, 1); if (k < 1 || k > pads) throw new FormatException($"line {n}: pad {k}: the {map.Console} has pads 1..{pads}"); cur = k - 1; maxPad = Math.Max(maxPad, cur); break; }
                case "mirror": { int k = Math.Clamp(I(0, 1), 1, pads); for (int m = k; m < mirror; m++) { held[m] = 0; sx[m] = sy[m] = 0; } mirror = k; maxPad = Math.Max(maxPad, k - 1); break; }
                case "pokeeach": { int k = I(0, 1); for (int i = 0; i < k; i++) { p.Steps.Add(new TasStep { Op = "poke", Args = new[] { a[1], a[2] }, Line = n }); Frame(0); } break; }
                case "pokehold": holds.RemoveAll(h => h.a == a[0]); holds.Add((a[0], a[1])); p.Steps.Add(new TasStep { Op = "poke", Args = new[] { a[0], a[1] }, Line = n }); break;
                case "pokeoff": if (a.Length == 0) holds.Clear(); else holds.RemoveAll(h => h.a == a[0]); break;
                default:
                    if (!RunnerOps.Contains(op) && !(consoleOp?.Invoke(op) ?? false))
                        throw new FormatException($"line {n}: unknown command '{op}' on the {map.Console}");
                    p.Steps.Add(new TasStep { Op = op, Args = a, Line = n });
                    break;
            }
        }
        p.PadsUsed = maxPad + 1;
        return p;
    }

    // ------------------------------------------------------------------ the per-frame log (.tlog)
    // tlog 1 <console> pads=N fps=F
    // F <frame> <pad1> [<pad2> ...]      pad = buttons[@x,y]   e.g. "A+B@-6,80" or "-"
    // @ <frame> <op> <args...>           an op before that frame (frame = frames already run)
    // The log is the exact expansion: running it gives the same checkpoints as running the script.

    public static void WriteLog(TasProgram p, TextWriter w)
    {
        w.WriteLine($"tlog 1 {p.Console} pads={p.PadsUsed} fps={p.Fps.ToString(CultureInfo.InvariantCulture)}");
        int f = 0;
        foreach (var s in p.Steps)
        {
            if (s.IsFrame)
            {
                w.Write("F "); w.Write(f.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < p.PadsUsed; i++) { w.Write(' '); w.Write(PadText(p.Map, s.Pads[i])); }
                w.WriteLine(); f++;
            }
            else w.WriteLine($"@ {f} {s.Op}{(s.Args.Length > 0 ? " " + string.Join(" ", s.Args) : "")}");
        }
    }

    public static string PadText(TasButtonMap m, TasPad p) => m.HasStick && (p.X != 0 || p.Y != 0) ? $"{m.Text(p.Buttons)}@{p.X},{p.Y}" : m.Text(p.Buttons);

    public static TasProgram ReadLog(string path, Func<string, bool>? consoleOp = null)
    {
        var lines = File.ReadAllLines(path);
        var h = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (h.Length < 3 || h[0] != "tlog") throw new FormatException($"{path}: not a TAS log (tlog 1 <console> ...)");
        var map = TasButtonMap.For(h[2]);
        int used = 1; double fps = 60;
        foreach (var kv in h.Skip(3)) { var e = kv.Split('='); if (e[0] == "pads") used = int.Parse(e[1]); else if (e[0] == "fps") fps = double.Parse(e[1], CultureInfo.InvariantCulture); }
        var p = new TasProgram { Console = map.Console, Map = map, Fps = fps, PadsUsed = used };
        int expect = 0;
        for (int n = 1; n < lines.Length; n++)
        {
            var t = lines[n].Split(' ', StringSplitOptions.RemoveEmptyEntries); if (t.Length == 0) continue;
            int fr = int.Parse(t[1], CultureInfo.InvariantCulture);
            if (fr != expect) throw new FormatException($"{path} line {n + 1}: frame {fr}, expected {expect}");
            if (t[0] == "F")
            {
                var pads = new TasPad[map.Pads];
                for (int i = 0; i < used && i + 2 < t.Length; i++)
                {
                    var s = t[i + 2]; int at = s.IndexOf('@'); sbyte x = 0, y = 0;
                    if (at >= 0) { var xy = s[(at + 1)..].Split(','); x = sbyte.Parse(xy[0]); y = sbyte.Parse(xy[1]); s = s[..at]; }
                    pads[i] = new TasPad(map.Parse(s, n + 1), x, y);
                }
                p.Steps.Add(new TasStep { Pads = pads, Line = n + 1 }); expect++;
            }
            else if (t[0] == "@")
            {
                if (!RunnerOps.Contains(t[2]) && !(consoleOp?.Invoke(t[2]) ?? false)) throw new FormatException($"{path} line {n + 1}: unknown op '{t[2]}'");
                p.Steps.Add(new TasStep { Op = t[2].ToLowerInvariant(), Args = t[3..], Line = n + 1 });
            }
            else throw new FormatException($"{path} line {n + 1}: expected F or @");
        }
        return p;
    }

    /// <summary>A frame list as a compact script (input BTNS N runs; stick lines when it moves). Used by the .bnm / .fm2 imports.</summary>
    public static IEnumerable<string> Compact(TasButtonMap map, IReadOnlyList<TasPad[]> frames, int pads)
    {
        int i = 0;
        var last = new TasPad[pads];
        while (i < frames.Count)
        {
            int j = i + 1;
            while (j < frames.Count && Same(frames[j], frames[i], pads)) j++;
            for (int k = 0; k < pads; k++)
            {
                if (pads > 1) yield return $"pad {k + 1}";
                var pd = frames[i][k];
                if (map.HasStick && (pd.X != last[k].X || pd.Y != last[k].Y)) yield return $"stick {pd.X} {pd.Y}";
                last[k] = pd;
                if (k < pads - 1) { yield return "release"; if (pd.Buttons != 0) yield return $"hold {map.Text(pd.Buttons)}"; }
                else yield return $"input {map.Text(pd.Buttons)} {j - i}";
            }
            i = j;
        }
    }

    private static bool Same(TasPad[] a, TasPad[] b, int n) { for (int i = 0; i < n; i++) if (!a[i].Equals(b[i])) return false; return true; }
}
