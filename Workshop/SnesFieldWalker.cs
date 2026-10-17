using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BrokenNes.Workshop;

/// <summary>
/// Reflection over a machine's object graph for the save-state test: every instance field of every object of the engine (the session, the board, the 65816, the S-PPU, the audio unit, the SPC700, the S-DSP and its voices,
/// the cartridge, the chip), down to the fields of structs and the elements of arrays of objects. A primitive, an enum, or an array of primitives is a <i>slot</i> (one "field"): a slot can be read, written, compared with the same
/// slot of another machine, and an array slot can be written at one element and compared as a whole. Used two ways: <b>comparing</b> a restored machine with the one that saved (any slot that differs is state nobody saves),
/// and <b>mutating</b> one slot at a time in the saving machine to see that the value comes back out of a load (a slot the loader does not restore comes back unchanged and is named).
/// <para>What is not a slot is declared, with its reason, in <see cref="Exclusions"/>: scratch buffers a line rewrites whole before reading, caches rebuilt on use, the game's ROM, the hooks of tools, queue storage whose position is not state.
/// Delegates and static fields are never state of a machine.</para>
/// </summary>
internal sealed class SnesFieldWalker
{
    public sealed class Slot
    {
        public string Path = "";
        public Type Type = typeof(int);
        public Func<object?> Get = () => null;
        public Action<object?> Set = _ => { };
        public bool IsArray => Type.IsArray;
    }

    /// <summary>A path pattern and the reason what it matches is not state of the machine.</summary>
    public sealed record Exclusion(string Pattern, string Reason)
    {
        public readonly Regex Regex = new(Pattern, RegexOptions.Compiled);
    }

    public readonly List<Slot> Slots = new();
    /// <summary>The exclusions that matched something, by pattern, with the number of paths each matched.</summary>
    public readonly Dictionary<Exclusion, int> Excluded = new();
    private readonly HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
    private readonly IReadOnlyList<Exclusion> exclusions;

    public SnesFieldWalker(object root, string rootName, IReadOnlyList<Exclusion> exclusions)
    {
        this.exclusions = exclusions;
        Walk(root, rootName, () => root, _ => { });
    }

    private static bool EngineType(Type t) =>
        t.Namespace != null && (t.Namespace.StartsWith("NesEmulator", StringComparison.Ordinal) || t.Namespace.StartsWith("BrokenNes", StringComparison.Ordinal))
        || t.FullName != null && t.FullName.StartsWith("System.ValueTuple", StringComparison.Ordinal);

    private void Walk(object? v, string path, Func<object?> get, Action<object?> set)
    {
        foreach (var ex in exclusions)
            if (ex.Regex.IsMatch(path)) { Excluded[ex] = Excluded.GetValueOrDefault(ex) + 1; return; }
        if (v == null) return;
        var t = v.GetType();
        if (typeof(Delegate).IsAssignableFrom(t) || t.IsPointer) return;
        if (t.IsPrimitive || t.IsEnum || t == typeof(decimal)) { Slots.Add(new Slot { Path = path, Type = t, Get = get, Set = set }); return; }
        if (t == typeof(string)) return;   // names and labels, not machine state
        if (t.IsArray)
        {
            var el = t.GetElementType()!;
            if (el.IsPrimitive || el.IsEnum) { Slots.Add(new Slot { Path = path, Type = t, Get = get, Set = set }); return; }
            var arr = (Array)v;
            if (arr.Rank != 1) throw new NotSupportedException($"{path}: a multi-dimensional array of {el.Name} is not handled by the walker");
            for (int i = 0; i < arr.Length; i++)
            {
                int k = i;
                Walk(arr.GetValue(k), $"{path}[{k}]", () => arr.GetValue(k), x => arr.SetValue(x, k));
            }
            return;
        }
        if (!EngineType(t)) return;   // framework types (lists, queues, streams) are not machine state here
        if (!t.IsValueType && !seen.Add(v)) return;
        for (var type = t; type != null && type != typeof(object); type = type.BaseType)
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (typeof(Delegate).IsAssignableFrom(f.FieldType) || f.FieldType.IsPointer || f.FieldType.IsByRef || f.FieldType.IsByRefLike) continue;
                string name = f.Name.StartsWith('<') ? f.Name[1..f.Name.IndexOf('>')] : f.Name;
                var field = f;
                if (t.IsValueType)
                    Walk(field.GetValue(get()), path + "." + name, () => field.GetValue(get()), x => { var box = get()!; field.SetValue(box, x); set(box); });
                else
                {
                    var obj = v;
                    Walk(field.GetValue(obj), path + "." + name, () => field.GetValue(obj), x => field.SetValue(obj, x));
                }
            }
    }

    // ------------------------------------------------------------------------------------------------------------------ comparing

    /// <summary>The first differing element of two values of one slot, or null when they are equal. Arrays of primitives compare as memory.</summary>
    public static string? Difference(Slot a, Slot b)
    {
        object? x = a.Get(), y = b.Get();
        if (x is Array ax && y is Array ay)
        {
            if (ax.Length != ay.Length) return $"length {ax.Length} vs {ay.Length}";
            var sx = AsBytes(ax); var sy = AsBytes(ay);
            if (sx.SequenceEqual(sy)) return null;
            int size = Math.Max(1, sx.Length / Math.Max(1, ax.Length));
            for (int i = 0; i < sx.Length; i++)
                if (sx[i] != sy[i]) { int e = i / size; return ax.Rank == 1 ? $"[{e}] {ax.GetValue(e)} vs {ay.GetValue(e)}" : $"[flat {e}]"; }
            return null;
        }
        return Equals(x, y) ? null : $"{x} vs {y}";
    }

    private static ReadOnlySpan<byte> AsBytes(Array a) =>
        System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(a), Buffer.ByteLength(a));

    /// <summary>The slots of two machines of the same shape that differ (paths and the first differing element), and how many slots were compared.</summary>
    public static (List<string> Diffs, int Compared, long Values) Compare(SnesFieldWalker a, SnesFieldWalker b)
    {
        var diffs = new List<string>();
        var byPath = b.Slots.ToDictionary(s => s.Path);
        int compared = 0; long values = 0;
        foreach (var sa in a.Slots)
        {
            if (!byPath.TryGetValue(sa.Path, out var sb)) { diffs.Add(sa.Path + " (missing in the other machine)"); continue; }
            compared++;
            values += sa.Get() is Array arr ? arr.Length : 1;
            string? d = Difference(sa, sb);
            if (d != null) diffs.Add($"{sa.Path} ({d})");
        }
        foreach (var sb in b.Slots) if (!a.Slots.Any(s => s.Path == sb.Path) && !diffs.Any(d => d.StartsWith(sb.Path))) diffs.Add(sb.Path + " (missing in the first machine)");
        return (diffs, compared, values);
    }

    // ------------------------------------------------------------------------------------------------------------------ mutating

    /// <summary>How many different candidates <see cref="Mutate"/> has per value (a loader may refuse one as out of range; the first that fits is used).</summary>
    public const int Attempts = 8;

    /// <summary>A different value of the same type: attempt 0-2 flip a low bit, 3 and 4 add and subtract one, 5 halves, 6 and 7 flip a higher bit. A loader's sanity check may refuse some; the next attempt is then tried.</summary>
    public static object Mutate(object v, int attempt)
    {
        long Pick(long x) => attempt switch { 0 => x ^ 1, 1 => x ^ 2, 2 => x ^ 4, 3 => x + 1, 4 => x - 1, 5 => x >> 1 == x ? x ^ 1 : x >> 1, 6 => x ^ 0x10, _ => x ^ 0x100 };
        switch (v)
        {
            case bool b: return !b;
            case byte x: return (byte)Pick(x);
            case sbyte x: return (sbyte)Pick(x);
            case short x: return (short)Pick(x);
            case ushort x: return (ushort)Pick(x);
            case int x: return (int)Pick(x);
            case uint x: return (uint)Pick(x);
            case long x: return Pick(x);
            case ulong x: return (ulong)Pick((long)x);
            case char x: return (char)Pick(x);
            case float x: return x + 1f + attempt;
            case double x: return x + 1.0 + attempt;
            case Enum e:
            {
                var values = Enum.GetValues(e.GetType()).Cast<object>().ToList();
                return values[(values.IndexOf(e) + 1 + attempt) % values.Count];
            }
        }
        throw new NotSupportedException("cannot mutate " + v.GetType());
    }

    public static object? ElementAt(Array arr, int i) => arr.GetValue(i);
}
