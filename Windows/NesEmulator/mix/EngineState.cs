using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NesEmulator;

/// <summary>
/// FA-ST-1: exact save states for the ported sound engines. A ported core is a stock unit (APU_FIX, APU_SFC, MdSoundUnit, SmsSound,
/// SmsFmSound) with an engine bolted on by a subclass; the stock base saves its own state, and this saves everything the subclass adds:
/// every instance field declared below the stock base, followed through the engine objects it holds (classes, structs, arrays, lists),
/// bit for bit, and restores it in place. Delegates (the engines' bus readers), threads/streams/reflection objects and the host
/// (Bus, CPUs, PPUs, mappers, other APUs) are not engine state and are left as they are. Shared objects and cycles keep their identity.
/// The section is framed by a magic number and a layout hash: a state from before FA-ST-1 (no section) or from a build whose engine
/// fields differ (hash mismatch) is reported by <see cref="TryLoad"/> returning false with a reason, and the caller then falls back to
/// the documented re-key (register replay) and says so on stderr: never silently.
/// </summary>
public static class EngineState
{
    private const ulong Magic = 0x31545345474E45B7UL;  // "\xB7ENGEST1"
    /// <summary>FA-ST-2 hub sections have a magic of their own: a stock unit's LoadState peeks for (and skips) an engine section right after its state, which is where a hub section stands.</summary>
    private const ulong HubMagic = 0x31425548474E45B7UL;  // ENGHUB1

    /// <summary>Test hook (Workshop --apu-channels --state negative control): loads skip the engine section, as an old state would.</summary>
    public static bool TestSkipRestore;

    /// <summary>Last load's outcome, for tests and logs.</summary>
    public static string LastLoadNote { get; private set; } = "";

    public static void Save(object root, Type stockBase, BinaryWriter w, bool units = false, object?[]? skip = null, ulong magic = Magic)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            new Walker(stockBase, units, skip).Write(bw, root);
        w.Write(magic); w.Write(LayoutHash(root.GetType(), stockBase)); w.Write((int)ms.Length); w.Write(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static byte[] Save(object root, Type stockBase, bool units = false) { using var ms = new MemoryStream(); using (var w = new BinaryWriter(ms)) Save(root, stockBase, w, units); return ms.ToArray(); }

    /// <summary>Reads an engine section if the stream holds one at this point (seekable streams: otherwise nothing is consumed), and restores it.
    /// False (with <see cref="LastLoadNote"/>) when there is none or it does not fit this build: the caller re-keys and reports.</summary>
    public static bool TryLoad(object root, Type stockBase, BinaryReader r, bool units = false, object?[]? skip = null, ulong magic = Magic)
    {
        var s = r.BaseStream;
        if (!s.CanSeek || s.Length - s.Position < 16) { LastLoadNote = "no engine section in the state (saved before FA-ST-1, or by another core)"; return false; }
        long at = s.Position;
        if (r.ReadUInt64() != magic) { s.Position = at; LastLoadNote = "no engine section in the state (saved before FA-ST-1, or by another core)"; return false; }
        ulong hash = r.ReadUInt64(); int len = r.ReadInt32(); byte[] body = r.ReadBytes(len);
        if (root is not IPortedEngineState && !units) { LastLoadNote = "engine section skipped (a stock unit loads a ported core's state)"; return false; }
        return Restore(root, stockBase, hash, body, units, skip);
    }

    /// <summary>True when the reader stands at an engine section (peek; nothing consumed).</summary>
    public static bool AtSection(BinaryReader r, ulong magic = Magic)
    {
        var s = r.BaseStream;
        if (!s.CanSeek || s.Length - s.Position < 16) return false;
        long at = s.Position; bool yes = r.ReadUInt64() == magic; s.Position = at; return yes;
    }

    /// <summary>For the stock bases' LoadState: reads (or skips) the engine section if there is one, restores a ported core's engine, and reports
    /// a ported core that had to keep its re-key.</summary>
    public static void LoadSection(object root, Type stockBase, BinaryReader r)
    {
        bool ok;
        try { ok = TryLoad(root, stockBase, r); }
        catch (Exception e) { ok = false; LastLoadNote = $"engine section unreadable ({e.GetType().Name}: {e.Message})"; }
        if (!ok && root is IPortedEngineState) ReportRekey(root);
    }

    /// <summary>For the stock bases' SaveState: a ported core appends its engine section.</summary>
    public static void SaveSection(object root, Type stockBase, BinaryWriter w) { if (root is IPortedEngineState) Save(root, stockBase, w); }

    public static bool TryLoad(object root, Type stockBase, byte[]? blob, bool units = false)
    {
        if (blob == null || blob.Length == 0) { LastLoadNote = "no engine section in the state (saved before FA-ST-1, or by another core)"; return false; }
        using var r = new BinaryReader(new MemoryStream(blob));
        return TryLoad(root, stockBase, r, units);
    }

    /// <summary>A NES sound bridge's state (APU_SNES, APU_DMG, APU_SMS, APU_MD, APU_SMSFM and their ported-engine ids): the front APU_FIX's state and
    /// the whole bridge (its channel model and the foreign unit it drives) as an engine section.</summary>
    public sealed class BridgeApuState { public object? fix; public string? eng; }

    public static object BridgeGetState(object bridge, IAPU front) =>
        new BridgeApuState { fix = front.GetState(), eng = Convert.ToBase64String(Save(bridge, typeof(object), units: true)) };

    /// <summary>FA-ST-2: another console's sound unit on a NES chip (NES:&lt;id&gt; through a hub bridge: NesApuOnSnes, GbApuOnNes, SmsSoundOnNes,
    /// SmsFmSoundOnNes, MdSoundOnNes). The console's own unit (<paramref name="front"/>) is saved by the session as before; this appends, after it,
    /// the bridge itself (its clocks, caches, queue, resampler; the front left out) and the NES chip it plays on (<paramref name="back"/>, whole,
    /// null when the chip belongs to a running NES that saves it itself), so a load continues sample for sample.</summary>
    public static void SaveHub(BinaryWriter w, object bridge, IAPU? back, params object?[] front)
    {
        Save(bridge, typeof(object), w, units: true, skip: front, magic: HubMagic);
        if (back != null) Save(back, typeof(object), w, units: true, magic: HubMagic);
    }

    /// <summary>Reads what <see cref="SaveHub"/> wrote, right after the front's state, and restores it over the bridge's own re-key (call it last).
    /// A state without it (saved before FA-ST-2) keeps the re-key and says so on stderr. True when restored.</summary>
    public static bool LoadHub(BinaryReader r, object bridge, IAPU? back, params object?[] front)
    {
        bool ok;
        try
        {
            ok = TryLoad(bridge, typeof(object), r, units: true, skip: front, magic: HubMagic);
            if (back != null && AtSection(r, HubMagic)) ok &= TryLoad(back, typeof(object), r, units: true, magic: HubMagic);
            else if (back != null) { ok = false; LastLoadNote = $"no NES chip section in the state (bridge: {(ok ? "restored" : LastLoadNote)}; at {r.BaseStream.Position} of {r.BaseStream.Length})"; }
        }
        catch (Exception e) { ok = false; LastLoadNote = $"hub section unreadable ({e.GetType().Name}: {e.Message})"; }
        if (!ok) ReportRekey(bridge);
        return ok;
    }

    /// <summary>Restores the front, runs the bridge's own re-key, then replaces it with the saved bridge (exact). An old state (the bare front)
    /// keeps the re-key and reports it.</summary>
    public static void BridgeSetState(object bridge, IAPU front, object state, Action rekey)
    {
        object inner = state; string? eng = null;
        if (state is BridgeApuState b) { inner = b.fix!; eng = b.eng; }
        else if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object && je.TryGetProperty("fix", out var f))
        { inner = f; if (je.TryGetProperty("eng", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String) eng = e.GetString(); }
        front.SetState(inner);
        rekey();
        bool ok;
        try { ok = TryLoad(bridge, typeof(object), eng == null ? null : Convert.FromBase64String(eng), units: true); }
        catch (Exception x) { ok = false; LastLoadNote = $"engine section unreadable ({x.GetType().Name}: {x.Message})"; }
        if (!ok) ReportRekey(bridge);
    }

    private static bool Restore(object root, Type stockBase, ulong hash, byte[] body, bool units, object?[]? skip = null)
    {
        if (TestSkipRestore) { LastLoadNote = "restore skipped (test negative control)"; return false; }
        if (hash != LayoutHash(root.GetType(), stockBase)) { LastLoadNote = $"engine section of another build or core ({root.GetType().Name} layout changed)"; return false; }
        using var br = new BinaryReader(new MemoryStream(body));
        new Walker(stockBase, units, skip).Read(br, root);
        LastLoadNote = "restored"; return true;
    }

    /// <summary>Reports a fallback to the re-key path (never silent: FA-ST-1).</summary>
    public static void ReportRekey(object root) =>
        Console.Error.WriteLine($"[EngineState] {root.GetType().Name}: {LastLoadNote}; engine re-keyed from the stock registers instead (not sample-exact)");

    /// <summary>A diagnostic: the types the walker reaches from <paramref name="root"/> and the section size (Workshop --apu-channels --state --audit).</summary>
    public static (int bytes, string[] types) Audit(object root, Type stockBase)
    {
        var wk = new Walker(stockBase, false) { Seen = new SortedSet<string>() };
        using var ms = new MemoryStream(); using (var bw = new BinaryWriter(ms)) wk.Write(bw, root);
        return ((int)ms.Length, wk.Seen.ToArray());
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static readonly Dictionary<(Type, Type?), FieldInfo[]> fieldCache = new();

    private static FieldInfo[] Fields(Type t, Type? stopAt)
    {
        lock (fieldCache)
        {
            if (fieldCache.TryGetValue((t, stopAt), out var f)) return f;
            var list = new List<FieldInfo>();
            for (var c = t; c != null && c != stopAt && c != typeof(object); c = c.BaseType)
                list.AddRange(c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(x => !IsSkippedStatic(x.FieldType)).OrderBy(x => x.Name, StringComparer.Ordinal));
            return fieldCache[(t, stopAt)] = list.ToArray();
        }
    }

    private static ulong LayoutHash(Type root, Type stockBase)
    {
        ulong h = 1469598103934665603UL;
        void Mix(string s) { foreach (char ch in s) { h ^= ch; h *= 1099511628211UL; } }
        Mix(root.FullName ?? root.Name);
        foreach (var f in Fields(root, stockBase)) { Mix(f.Name); Mix(f.FieldType.FullName ?? f.FieldType.Name); }
        return h;
    }

    /// <summary>Field types that are never state, decided from the declared type so save and load agree.</summary>
    private static bool IsSkippedStatic(Type t) =>
        typeof(Delegate).IsAssignableFrom(t) || t.IsPointer || t == typeof(IntPtr) || t == typeof(UIntPtr) || t.IsByRefLike;

    /// <summary>Objects that belong to the host or the runtime, not the engine (decided from the runtime type).</summary>
    private static bool IsHost(Type t, object root, bool units)
    {
        if (typeof(Delegate).IsAssignableFrom(t) || t == typeof(object) || typeof(Type).IsAssignableFrom(t) || typeof(MemberInfo).IsAssignableFrom(t)) return true;
        string ns = t.Namespace ?? "";
        if (ns.StartsWith("System.Threading") || ns.StartsWith("System.IO") || ns.StartsWith("System.Reflection") || ns.StartsWith("System.Runtime") || ns.StartsWith("System.Diagnostics") || ns.StartsWith("System.Net")) return true;
        if (typeof(Bus).IsAssignableFrom(t) || typeof(NES).IsAssignableFrom(t) || typeof(ICPU).IsAssignableFrom(t) || typeof(IPPU).IsAssignableFrom(t)
            || typeof(IMapper).IsAssignableFrom(t) || typeof(Cartridge).IsAssignableFrom(t)) return true;
        if (typeof(IAPU).IsAssignableFrom(t)) return true;
        if (!units && (typeof(Snes.APU_SFC).IsAssignableFrom(t) || typeof(Sega.MdSoundUnit).IsAssignableFrom(t) || typeof(Sega.ISmsSound).IsAssignableFrom(t))) return true; // another unit (the root itself is a back-reference)
        return false;
    }

    private static bool IsBlittable(Type t) => t.IsPrimitive || t.IsEnum;

    private sealed class Walker
    {
        private readonly Type stockBase; private readonly bool units;
        private readonly HashSet<object>? skip;
        public SortedSet<string>? Seen;
        private readonly Dictionary<object, int> ids = new(ReferenceEqualityComparer.Instance);
        private readonly List<object> objs = new();
        private object root = null!;
        public Walker(Type stockBase, bool units, object?[]? skip = null)
        {
            this.stockBase = stockBase; this.units = units;
            if (skip != null) { this.skip = new HashSet<object>(ReferenceEqualityComparer.Instance); foreach (var o in skip) if (o != null) this.skip.Add(o); }
        }

        // tags for references
        private const byte Null = 0, Back = 1, New = 2, HostRef = 3, Static = 4;

        // Objects held by static fields (shared wave tables, e.g. InfoNES's pulse_50 that a channel field points at) are saved as a reference to the
        // static, never restored in place: copying a saved table into the one the field happens to point at now would corrupt the shared table.
        private static readonly Dictionary<object, string> staticKey = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<Type> staticScanned = new();
        private static void ScanStatics(Type t)
        {
            lock (staticKey)
            {
                for (var c = t; c != null && c != typeof(object); c = c.BaseType)
                {
                    if (!staticScanned.Add(c)) continue;
                    if (c.Assembly != typeof(EngineState).Assembly) continue;
                    foreach (var f in c.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType.IsValueType || f.IsLiteral || IsSkippedStatic(f.FieldType) || c.ContainsGenericParameters) continue;
                        object? v; try { v = f.GetValue(null); } catch { continue; }
                        if (v == null || v is string) continue;
                        string key = c.AssemblyQualifiedName + "|" + f.Name;
                        staticKey.TryAdd(v, key);
                        if (v is object?[] arr) for (int i = 0; i < arr.Length; i++) if (arr[i] != null && arr[i] is not string) staticKey.TryAdd(arr[i]!, key + "|" + i);
                    }
                }
            }
        }
        private static object ResolveStatic(string key)
        {
            var parts = key.Split('|');
            var t = Type.GetType(parts[0], throwOnError: true)!;
            var v = t.GetField(parts[1], BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
            return parts.Length > 2 ? ((object?[])v)[int.Parse(parts[2])]! : v;
        }

        public void Write(BinaryWriter w, object r) { root = r; ids[r] = 0; objs.Add(r); WriteFields(w, r, Fields(r.GetType(), stockBase)); }
        public void Read(BinaryReader rd, object r) { root = r; objs.Add(r); object o = r; ReadFields(rd, ref o, Fields(r.GetType(), stockBase)); }

        private void WriteFields(BinaryWriter w, object o, FieldInfo[] fs) { ScanStatics(o.GetType()); foreach (var f in fs) WriteValue(w, f.FieldType, f.GetValue(o)); }

        private void ReadFields(BinaryReader r, ref object o, FieldInfo[] fs)
        {
            ScanStatics(o.GetType());
            foreach (var f in fs)
            {
                object? cur = f.GetValue(o);
                object? v = ReadValue(r, f.FieldType, cur);
                if (!ReferenceEquals(v, cur) || f.FieldType.IsValueType) f.SetValue(o, v);
            }
        }

        private void WriteValue(BinaryWriter w, Type declared, object? v)
        {
            if (IsSkippedStatic(declared)) return;
            if (declared.IsValueType)
            {
                if (IsBlittable(declared)) { WritePrim(w, declared.IsEnum ? Enum.GetUnderlyingType(declared) : declared, v!); return; }
                if (Nullable.GetUnderlyingType(declared) is Type nu) { w.Write(v != null); if (v != null) WriteValue(w, nu, v); return; }
                Seen?.Add(declared.FullName!);
                WriteFields(w, v!, Fields(declared, null)); return;
            }
            if (v == null) { w.Write(Null); return; }
            if (ids.TryGetValue(v, out int id)) { w.Write(Back); w.Write(id); return; }
            string? sk; lock (staticKey) staticKey.TryGetValue(v, out sk);
            if (sk != null) { w.Write(Static); w.Write(sk); return; }
            var t = v.GetType();
            if (IsHost(t, root, units) || IsSkippedStatic(t) || skip?.Contains(v) == true) { w.Write(HostRef); return; }
            w.Write(New); w.Write(t.AssemblyQualifiedName!);
            ids[v] = objs.Count; objs.Add(v);
            if (t == typeof(string)) { w.Write((string)v); return; }
            Seen?.Add(t.FullName!);
            if (t.IsArray)
            {
                var a = (Array)v; var et = t.GetElementType()!;
                w.Write(a.Rank); for (int d = 0; d < a.Rank; d++) w.Write(a.GetLength(d));
                if (IsBlittable(et)) { var bytes = new byte[Buffer.ByteLength(a)]; Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length); w.Write(bytes); }
                else if (a.Rank == 1) for (int i = 0; i < a.Length; i++) WriteValue(w, et, a.GetValue(i));
                else foreach (var x in a) WriteValue(w, et, x);
                return;
            }
            WriteFields(w, v, Fields(t, null));
        }

        private object? ReadValue(BinaryReader r, Type declared, object? cur)
        {
            if (IsSkippedStatic(declared)) return cur;
            if (declared.IsValueType)
            {
                if (IsBlittable(declared)) { var p = ReadPrim(r, declared.IsEnum ? Enum.GetUnderlyingType(declared) : declared); return declared.IsEnum ? Enum.ToObject(declared, p) : p; }
                if (Nullable.GetUnderlyingType(declared) is Type nu) return r.ReadBoolean() ? ReadValue(r, nu, null) : null;
                object box = cur ?? RuntimeHelpers.GetUninitializedObject(declared);
                ReadFields(r, ref box, Fields(declared, null)); return box;
            }
            byte tag = r.ReadByte();
            switch (tag)
            {
                case Null: return null;
                case Back: return objs[r.ReadInt32()];
                case HostRef: return cur;
                case Static: return ResolveStatic(r.ReadString());
            }
            var t = Type.GetType(r.ReadString(), throwOnError: true)!;
            if (t == typeof(string)) { var s = r.ReadString(); objs.Add(s); return s; }
            if (t.IsArray)
            {
                var et = t.GetElementType()!; int rank = r.ReadInt32(); var lens = new int[rank]; for (int d = 0; d < rank; d++) lens[d] = r.ReadInt32();
                Array a = cur is Array ca && ca.GetType() == t && Enumerable.Range(0, rank).All(d => ca.GetLength(d) == lens[d]) ? ca : Array.CreateInstance(et, lens);
                objs.Add(a);
                if (IsBlittable(et)) { int n = Buffer.ByteLength(a); var bytes = r.ReadBytes(n); Buffer.BlockCopy(bytes, 0, a, 0, n); }
                else if (rank == 1) for (int i = 0; i < a.Length; i++) a.SetValue(ReadValue(r, et, a.GetValue(i)), i);
                else
                {
                    var idx = new int[rank];
                    for (int k = 0; k < a.Length; k++)
                    {
                        int rem = k; for (int d = rank - 1; d >= 0; d--) { idx[d] = rem % lens[d]; rem /= lens[d]; }
                        a.SetValue(ReadValue(r, et, a.GetValue(idx)), idx);
                    }
                }
                return a;
            }
            object o = cur != null && cur.GetType() == t ? cur : RuntimeHelpers.GetUninitializedObject(t);
            objs.Add(o);
            ReadFields(r, ref o, Fields(t, null));
            return o;
        }

        private static void WritePrim(BinaryWriter w, Type t, object v)
        {
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: w.Write((bool)v); break; case TypeCode.Byte: w.Write((byte)v); break; case TypeCode.SByte: w.Write((sbyte)v); break;
                case TypeCode.Char: w.Write((ushort)(char)v); break; case TypeCode.Int16: w.Write((short)v); break; case TypeCode.UInt16: w.Write((ushort)v); break;
                case TypeCode.Int32: w.Write((int)v); break; case TypeCode.UInt32: w.Write((uint)v); break; case TypeCode.Int64: w.Write((long)v); break;
                case TypeCode.UInt64: w.Write((ulong)v); break; case TypeCode.Single: w.Write((float)v); break; case TypeCode.Double: w.Write((double)v); break;
                default: throw new NotSupportedException(t.FullName);
            }
        }

        private static object ReadPrim(BinaryReader r, Type t) => Type.GetTypeCode(t) switch
        {
            TypeCode.Boolean => r.ReadBoolean(), TypeCode.Byte => r.ReadByte(), TypeCode.SByte => r.ReadSByte(), TypeCode.Char => (char)r.ReadUInt16(),
            TypeCode.Int16 => r.ReadInt16(), TypeCode.UInt16 => r.ReadUInt16(), TypeCode.Int32 => r.ReadInt32(), TypeCode.UInt32 => r.ReadUInt32(),
            TypeCode.Int64 => r.ReadInt64(), TypeCode.UInt64 => r.ReadUInt64(), TypeCode.Single => r.ReadSingle(), TypeCode.Double => r.ReadDouble(),
            _ => throw new NotSupportedException(t.FullName)
        };
    }
}

/// <summary>Marks a ported sound core whose engine (the fields its class adds to the stock unit) is saved and restored by <see cref="EngineState"/> (FA-ST-1).</summary>
public interface IPortedEngineState { }
