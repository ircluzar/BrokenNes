using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NesEmulator.Debugging.Movies;

/// <summary>
/// The state diff by symbol (milestone D5): two captures of a CPU's data spaces compared variable by variable ("these 6 variables differ"),
/// then the bytes no symbol names, per space. A variable is a symbol bound to a data space of the CPU (never ROM); NESFab's <c>name@0</c>,
/// <c>name@1</c> bytes are one variable. The MCP server's diff_states, Workshop's --movie diverge and BrokenDev bisect all use this.
/// </summary>
public static class SymbolDiff
{
    public sealed class Variable
    {
        public string Name = "", Place = "";
        public byte[] A = Array.Empty<byte>(), B = Array.Empty<byte>();
        /// <summary>The little-endian values when the variable is 1 to 4 bytes, else null.</summary>
        public long? ValueA, ValueB;
    }

    public sealed class SpaceDiff
    {
        public string Space = "";
        public int Differing, WithoutSymbol;
        public List<string> FirstWithoutSymbol = new();
    }

    public sealed class Result
    {
        public int Symbols, VariablesDiffering;
        public List<Variable> Variables = new();
        public List<SpaceDiff> Spaces = new();
        public bool Same => VariablesDiffering == 0 && Spaces.Count == 0;
    }

    /// <summary>Copies every data space of the CPU (not ROM / PRG, none over 2 MB): what <see cref="Compare"/> takes. Between frames or parked only.</summary>
    public static Dictionary<int, byte[]> Capture(DebugCpu c)
    {
        var d = new Dictionary<int, byte[]>();
        foreach (var sp in c.Spaces)
        {
            if (sp.Size > 2 * 1024 * 1024 || IsRom(sp)) continue;
            var b = new byte[sp.Size];
            for (uint i = 0; i < sp.Size; i++) b[i] = c.PeekSpace(sp.Id, i);
            d[sp.Id] = b;
        }
        return d;
    }

    private static bool IsRom(AddressSpace sp) => sp.Name is "rom" or "prg";

    /// <param name="limit">at most this many variables are listed (all are counted)</param>
    public static Result Compare(DebugCpu c, SymbolTable symbols, Dictionary<int, byte[]> a, Dictionary<int, byte[]> b, int limit = 200) =>
        Compare(c, symbols, a, c, symbols, b, limit);

    /// <summary>
    /// Two machines, each with its own symbols (two builds of a game: a variable may live at another address in each). Variables are matched by
    /// name; a variable only one side has is listed with the other side empty. The bytes without a symbol are compared per space by address,
    /// only when both sides have the same symbols (the same build).
    /// </summary>
    public static Result Compare(DebugCpu ca, SymbolTable sa, Dictionary<int, byte[]> a, DebugCpu cb, SymbolTable sb, Dictionary<int, byte[]> b, int limit = 200)
    {
        var res = new Result { Symbols = sa.All.Count };
        var va = Variables(ca, sa, a);
        var vb = ReferenceEquals(sa, sb) && ReferenceEquals(ca, cb) ? va : Variables(cb, sb, b);
        var coveredA = new HashSet<(int, uint)>();
        foreach (var name in va.Keys.Union(vb.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            var (bytesA, placeA) = va.TryGetValue(name, out var pa) ? Read(ca, pa, a, coveredA) : (Array.Empty<byte>(), "");
            var (bytesB, placeB) = vb.TryGetValue(name, out var pb) ? Read(cb, pb, b, null) : (Array.Empty<byte>(), "");
            if (bytesA.SequenceEqual(bytesB)) continue;
            res.VariablesDiffering++;
            if (res.Variables.Count >= limit) continue;
            var v = new Variable { Name = name, Place = placeA == placeB || placeB.Length == 0 ? placeA : placeA.Length == 0 ? placeB : $"{placeA} / {placeB}", A = bytesA, B = bytesB };
            if (bytesA.Length is >= 1 and <= 4 && bytesB.Length == bytesA.Length) { v.ValueA = Le(bytesA); v.ValueB = Le(bytesB); }
            res.Variables.Add(v);
        }
        bool sameBuild = ReferenceEquals(sa, sb) || sa.All.Count == sb.All.Count && sa.All.Zip(sb.All).All(p => p.First.Name == p.Second.Name && p.First.Address == p.Second.Address);
        if (sameBuild)
            foreach (var (id, spa) in a.OrderBy(x => x.Key))
            {
                if (!b.TryGetValue(id, out var spb)) continue;
                var sd = new SpaceDiff { Space = ca.Spaces.First(x => x.Id == id).Name };
                int n = Math.Min(spa.Length, spb.Length);
                for (int i = 0; i < n; i++)
                {
                    if (spa[i] == spb[i]) continue;
                    sd.Differing++;
                    if (coveredA.Contains((id, (uint)i))) continue;
                    sd.WithoutSymbol++;
                    if (sd.FirstWithoutSymbol.Count < 16) sd.FirstWithoutSymbol.Add($"${i:X4}: {spa[i]:X2} -> {spb[i]:X2}");
                }
                if (sd.Differing > 0) res.Spaces.Add(sd);
            }
        return res;
    }

    private static long Le(byte[] b) { long v = 0; for (int i = b.Length - 1; i >= 0; i--) v = v << 8 | b[i]; return v; }

    private static Dictionary<string, List<(int idx, SpaceKey key, int size)>> Variables(DebugCpu c, SymbolTable symbols, Dictionary<int, byte[]> spaces)
    {
        var vars = new Dictionary<string, List<(int idx, SpaceKey key, int size)>>();
        foreach (var s in symbols.All)
        {
            if (!c.BindSymbol(s, out var key) || !spaces.ContainsKey(key.Space)) continue;
            var sp = c.Spaces.First(x => x.Id == key.Space);
            if (IsRom(sp)) continue;
            var m = Regex.Match(s.Name, @"^(.*)@(\d+)$");
            string name = m.Success ? m.Groups[1].Value : s.Name;
            int idx = m.Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (!vars.TryGetValue(name, out var l)) vars[name] = l = new();
            l.Add((idx, key, m.Success ? 1 : Math.Clamp(s.Size, 1, 256)));
        }
        return vars;
    }

    private static (byte[] bytes, string place) Read(DebugCpu c, List<(int idx, SpaceKey key, int size)> parts, Dictionary<int, byte[]> spaces, HashSet<(int, uint)>? covered)
    {
        var o = new List<byte>();
        string? place = null;
        foreach (var (_, key, size) in parts.OrderBy(p => p.idx))
            for (int i = 0; i < size; i++)
            {
                uint off = key.Offset + (uint)i;
                var sp = spaces[key.Space];
                if (off >= sp.Length) break;
                o.Add(sp[off]); covered?.Add((key.Space, off));
                place ??= $"{c.Spaces.First(x => x.Id == key.Space).Name}:${off:X4}";
            }
        return (o.ToArray(), place ?? "");
    }

    /// <summary>The result in a few lines of text.</summary>
    public static IEnumerable<string> Describe(Result r, string nameA = "a", string nameB = "b", int lines = 40)
    {
        if (r.Same) { yield return "no difference"; yield break; }
        yield return $"{r.VariablesDiffering} variable(s) differ ({r.Symbols} symbols loaded)";
        foreach (var v in r.Variables.Take(lines))
            yield return v.ValueA is long va && v.ValueB is long vb
                ? $"  {v.Name} ({v.Place}): {nameA} {va} (${va:X}) {nameB} {vb} (${vb:X})"
                : $"  {v.Name} ({v.Place}, {v.A.Length} bytes): {nameA} {Hex(v.A)} {nameB} {Hex(v.B)}";
        if (r.Variables.Count > lines) yield return $"  ... {r.Variables.Count - lines} more";
        foreach (var s in r.Spaces)
            yield return $"  space {s.Space}: {s.Differing} byte(s) differ, {s.WithoutSymbol} without a symbol" + (s.FirstWithoutSymbol.Count > 0 ? ": " + string.Join(", ", s.FirstWithoutSymbol.Take(8)) : "");
    }

    private static string Hex(byte[] b) => b.Length <= 16 ? string.Join(" ", b.Select(x => x.ToString("X2"))) : string.Join(" ", b.Take(16).Select(x => x.ToString("X2"))) + " ...";
}
