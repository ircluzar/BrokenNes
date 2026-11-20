// [cer64plus] Savestates by reflection over the live object graph rooted at N64System.
//
// Rules:
// - Values (primitives, enums, strings, primitive arrays, structs) are written field by field in a fixed order
//   (fields sorted by metadata token, base classes first).
// - Owning UnmanagedBuffers (native memory: RDRAM, SP memory, register blocks, GPR/FPR/vector files) are saved as raw
//   bytes and restored in place, so every pointer into them stays valid. Slices (buffers with a parent) are skipped.
// - References are saved as object ids in walk order. On load, a reference to an id already seen is re-pointed to that
//   object (this restores aliases such as RDP combiner inputs); a new id adopts the object currently in the field.
// - Skipped: statics, delegates/events, pointers, threads, streams, loggers, reflection/runtime objects, the memory map,
//   debug-only objects and rebuildable caches. Static Telemetry counters are saved explicitly.
// Saves and loads must happen between execution batches (no instruction half-executed).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using cor64.IO;

namespace cor64
{
    public static class SaveState
    {
        private const string Magic = "COR64ST2"; // v2: named, length-prefixed fields; refs distinct from definitions

        private static readonly HashSet<string> s_SkipFieldNames = new() {
            // rebuildable caches and memory-map plumbing
            "m_DecodeRaw", "m_DecodeAddr", "m_DecodeEntry", "m_MemMap", "m_BaseMap", "m_ReadTable", "m_WriteTable",
            "m_ReadNotify", "m_WriteNotify", "m_MappedDevices", "m_IsPreReadMapped", "m_IsPreWriteMapped",
            "m_Hooks", "m_CapturedBlocks", "m_Profiler", "m_RomBuffer", "m_CartRom", "m_ValueBuffer",
            "m_WriteMap", "m_ReadMap", "CallHack", "m_ImemPtr", "m_DmemPtr", "m_RdramPtr", "m_FetchChecks", "m_FetchFastOff", "m_FastFetchChecks", "m_FastFetchOff",
            "m_Checks", "m_FastOff", "m_DmaEngine", "m_Disassembler", "m_Debugger", "m_Cartridge", "m_BootManager",
            "Disassembler", "TraceLog", "m_JitBlocks", "m_TraceLog", "CallTable", "m_CallTable", "m_Scripts",
        };

        // [emu_fix3] COR64_STATE_NORECCACHE=0: the CPU block cache (m_RecPages) is saved and loaded like any other field,
        // as before. On: it is skipped. Loading it overwrote the live CpuRecBlocks' fields (XId, Retired, ...) with the
        // saved ones while their native code (IntPtr, never saved) stayed, so the RecClear after the load retired native
        // code under ids from the saving session: ids past s_XById (IndexOutOfRangeException in XIdReclaim on the second
        // loadstate of a compat session) or ids of other live blocks (two blocks sharing one link id).
        private static readonly bool s_NoRecCache = Environment.GetEnvironmentVariable("COR64_STATE_NORECCACHE") != "0";
        static SaveState() { if (s_NoRecCache) s_SkipFieldNames.Add("m_RecPages"); }

        private static readonly string[] s_SkipNamespaces = {
            "System.Threading", "System.IO", "System.Reflection", "System.Text", "System.Diagnostics", "System.Runtime",
            "NLog", "Veldrid", "GLFW", "OpenTK", "ImGuiNET", "cor64.Debugging", "cor64.BassSharp", "cor64.Mips.Analysis",
            "cor64.Mips.R4300I.JitIL", "cor64.HLE",
        };

        private static readonly Dictionary<Type, FieldInfo[]> s_FieldCache = new();
        [ThreadStatic] private static int s_Discard;
        private static readonly bool s_Trace = Environment.GetEnvironmentVariable("COR64_SAVESTATE_TRACE") == "1";

        public static void Save(N64System system, string path)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            Save(system, fs);
        }

        public static void Load(N64System system, string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            Load(system, fs);
        }

        public static void Save(N64System system, Stream stream)
        {
            system.DeviceRcp.RcpInterface?.FlushDmaCompletions(); // [timing] pending PI/SI completions are not saved
            system.DeviceRcp.DeviceRdp?.BeforeStateSave(); // [cer64plus] drain GPU work in flight; copy GPU-only memory into saved fields
            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            w.Write(Magic);
            w.Write(system.AttachedCartridge?.Crc1 ?? 0u);
            WriteTelemetry(w);
            var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            if (s_TraceDir != null) s_TraceW = new StreamWriter(Path.Combine(s_TraceDir, $"fields_{System.Threading.Interlocked.Increment(ref s_TraceN)}.txt"));
            try { WriteObject(w, system, ids); } finally { s_TraceW?.Dispose(); s_TraceW = null; }
        }

        public static void Load(N64System system, Stream stream)
        {
            cor64.Mips.R4300I.SystemController.Boot1End(); // [boot_fix1] savestates keep their game's Count rate
            system.DeviceRcp.DeviceRdp?.Drain(); // [cer64plus] ...or would land in RDRAM after it was restored
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            if (r.ReadString() != Magic) throw new InvalidDataException("Not a cor64 savestate");
            uint crc = r.ReadUInt32();
            if (system.AttachedCartridge != null && crc != system.AttachedCartridge.Crc1)
                throw new InvalidDataException("Savestate was made with a different ROM");
            ReadTelemetry(r);
            var objs = new Dictionary<int, object>();
            ReadObjectInto(r, system, objs, 0);
            system.AfterStateLoad();
            system.DeviceRcp.DeviceRdp?.AfterStateLoad();
        }

        // ---------------- telemetry statics ----------------
        private static void WriteTelemetry(BinaryWriter w)
        {
            w.Write(Telemetry.Ticks); w.Write(Telemetry.ViInterrupts); w.Write(Telemetry.AiLenWrites); w.Write(Telemetry.AiBytes);
        }

        private static void ReadTelemetry(BinaryReader r)
        {
            Telemetry.Ticks = r.ReadInt64(); Telemetry.ViInterrupts = r.ReadInt64(); Telemetry.AiLenWrites = r.ReadInt64(); Telemetry.AiBytes = r.ReadInt64();
        }

        // ---------------- type rules ----------------
        private static bool SkipType(Type t)
        {
            if (t.IsPointer || t == typeof(IntPtr) || t == typeof(UIntPtr)) return true;
            if (typeof(Delegate).IsAssignableFrom(t)) return true;
            if (t == typeof(Type) || typeof(MemberInfo).IsAssignableFrom(t)) return true;
            if (t == typeof(object)) return true;
            if (t.IsValueType && !t.IsPrimitive && !t.IsEnum && t != typeof(decimal) && (t.Namespace == "System") &&
                !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))) return true;
            if (t.IsGenericType) foreach (var ga in t.GetGenericArguments()) if (SkipType(ga)) return true;
            if (t == typeof(cor64.Mips.Opcode) || t == typeof(cor64.Mips.DecodedInstruction)) return true;
            if (t.FullName != null && (t.FullName.StartsWith("cor64.Rdp.RdpCommand", StringComparison.Ordinal) || t.FullName.StartsWith("cor64.Rdp.Commands.", StringComparison.Ordinal))) return true;
            string ns = t.Namespace ?? "";
            foreach (var s in s_SkipNamespaces) if (ns.StartsWith(s, StringComparison.Ordinal)) return true;
            if (t.IsArray) return SkipType(t.GetElementType());
            return false;
        }

        private static bool IsSimple(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(decimal) || t == typeof(string);

        private static FieldInfo[] Fields(Type t)
        {
            if (s_FieldCache.TryGetValue(t, out var f)) return f;
            var chain = new List<Type>();
            for (var c = t; c != null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType) chain.Insert(0, c);
            var list = new List<FieldInfo>();
            foreach (var c in chain) {
                foreach (var fi in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(x => x.MetadataToken)) {
                    if (fi.IsDefined(typeof(NonSerializedAttribute))) continue;
                    if (s_SkipFieldNames.Contains(fi.Name)) continue;
                    if (fi.Name.Contains("k__BackingField") && s_SkipFieldNames.Contains(fi.Name.Split('<', '>')[1])) continue;
                    if (SkipType(fi.FieldType)) continue;
                    list.Add(fi);
                }
            }
            f = list.ToArray();
            s_FieldCache[t] = f;
            return f;
        }

        // ---------------- simple values ----------------
        private static void WriteSimple(BinaryWriter w, Type t, object v)
        {
            if (t == typeof(string)) { w.Write(v != null); if (v != null) w.Write((string)v); return; }
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            switch (Type.GetTypeCode(t)) {
                case TypeCode.Boolean: w.Write((bool)v); break;
                case TypeCode.Byte: w.Write((byte)v); break;
                case TypeCode.SByte: w.Write((sbyte)v); break;
                case TypeCode.Int16: w.Write((short)v); break;
                case TypeCode.UInt16: w.Write((ushort)v); break;
                case TypeCode.Int32: w.Write((int)v); break;
                case TypeCode.UInt32: w.Write((uint)v); break;
                case TypeCode.Int64: w.Write((long)v); break;
                case TypeCode.UInt64: w.Write((ulong)v); break;
                case TypeCode.Single: w.Write((float)v); break;
                case TypeCode.Double: w.Write((double)v); break;
                case TypeCode.Char: w.Write((char)v); break;
                case TypeCode.Decimal: w.Write((decimal)v); break;
                default: throw new NotSupportedException(t.FullName);
            }
        }

        private static object ReadSimple(BinaryReader r, Type t)
        {
            if (t == typeof(string)) return r.ReadBoolean() ? r.ReadString() : null;
            Type u = t.IsEnum ? Enum.GetUnderlyingType(t) : t;
            object v = Type.GetTypeCode(u) switch {
                TypeCode.Boolean => r.ReadBoolean(), TypeCode.Byte => r.ReadByte(), TypeCode.SByte => r.ReadSByte(),
                TypeCode.Int16 => r.ReadInt16(), TypeCode.UInt16 => r.ReadUInt16(), TypeCode.Int32 => r.ReadInt32(),
                TypeCode.UInt32 => r.ReadUInt32(), TypeCode.Int64 => r.ReadInt64(), TypeCode.UInt64 => r.ReadUInt64(),
                TypeCode.Single => r.ReadSingle(), TypeCode.Double => r.ReadDouble(), TypeCode.Char => r.ReadChar(),
                TypeCode.Decimal => r.ReadDecimal(), _ => throw new NotSupportedException(t.FullName)
            };
            return t.IsEnum ? Enum.ToObject(t, v) : v;
        }

        // ---------------- write ----------------
        private static void WriteValue(BinaryWriter w, Type t, object v, Dictionary<object, int> ids)
        {
            if (IsSimple(t)) { WriteSimple(w, t, v); return; }
            var nu = Nullable.GetUnderlyingType(t);
            if (nu != null) { w.Write(v != null); if (v != null) WriteValue(w, nu, v, ids); return; }
            if (t.IsValueType) { WriteStruct(w, t, v, ids); return; }
            // reference
            if (v == null) { w.Write(-1); return; }
            if (ids.TryGetValue(v, out int id)) { w.Write(-(id + 2)); return; } // reference to an earlier definition
            id = ids.Count; ids[v] = id;
            w.Write(id);                                                         // definition follows
            WriteObjectBody(w, v, ids);
        }

        private static void WriteObject(BinaryWriter w, object root, Dictionary<object, int> ids) => WriteValue(w, root.GetType(), root, ids);

        private static void WriteStruct(BinaryWriter w, Type t, object v, Dictionary<object, int> ids) => WriteFields(w, t, v, ids);

        // Each field: name, byte length, value. Lets a load skip fields that no longer exist and keep defaults for new ones.
        private static void WriteFields(BinaryWriter w, Type t, object v, Dictionary<object, int> ids)
        {
            var fields = Fields(t);
            s_TraceDepth++;
            try { WriteFieldsInner(w, t, v, ids, fields); } finally { s_TraceDepth--; }
        }
        [ThreadStatic] private static int s_TraceDepth;
        [ThreadStatic] private static StreamWriter s_TraceW;
        private static readonly string s_TraceDir = Environment.GetEnvironmentVariable("COR64_SAVESTATE_FIELDHASH");
        private static int s_TraceN;

        private static void WriteFieldsInner(BinaryWriter w, Type t, object v, Dictionary<object, int> ids, FieldInfo[] fields)
        {
            w.Write(fields.Length);
            foreach (var f in fields) {
                w.Write(f.Name);
                using var ms = new MemoryStream();
                using (var fw = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) {
                    try { WriteValue(fw, f.FieldType, f.GetValue(v), ids); }
                    catch (Exception e) when (e is not SaveStatePathException) { throw new SaveStatePathException(t.FullName + "." + f.Name, e); }
                }
                w.Write((int)ms.Length);
                if (s_Trace && ms.Length > 262144) Console.WriteLine("SAVESTATE field {0}.{1}: {2} bytes", t.Name, f.Name, ms.Length);
                if (s_TraceW != null) { // [session_leak] debug: per-field hash listing (COR64_SAVESTATE_FIELDHASH=<dir>)
                    ulong h = 1469598103934665603UL; foreach (byte bb in ms.GetBuffer().AsSpan(0, (int)ms.Length)) { h ^= bb; h *= 1099511628211UL; }
                    s_TraceW.WriteLine($"{s_TraceDepth} {t.FullName}.{f.Name} {ms.Length} {h:X16}");
                }
                ms.Position = 0; ms.CopyTo(w.BaseStream);
            }
        }

        private static unsafe void WriteObjectBody(BinaryWriter w, object v, Dictionary<object, int> ids)
        {
            var t = v.GetType();
            w.Write(t.FullName);
            if (v is UnmanagedBuffer ub) {
                bool owner = ub.ParentBuffer == null;
                w.Write(owner);
                if (owner) { w.Write(ub.Size); w.Write(new ReadOnlySpan<byte>((void*)ub.GetPointer(), ub.Size)); }
                return;
            }
            if (t.IsArray) {
                var a = (Array)v; var et = t.GetElementType();
                if (a.Rank != 1) throw new NotSupportedException("multi-dimensional array " + t);
                w.Write(a.Length);
                if (et.IsPrimitive) { var bytes = new byte[Buffer.ByteLength(a)]; Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length); w.Write(bytes.Length); w.Write(bytes); }
                else if (SkipType(et)) { }
                else for (int i = 0; i < a.Length; i++) WriteValue(w, et, a.GetValue(i), ids);
                return;
            }
            WriteFields(w, t, v, ids);
        }

        // ---------------- read ----------------
        private static object ReadValue(BinaryReader r, Type t, object current, Dictionary<int, object> objs)
        {
            if (IsSimple(t)) return ReadSimple(r, t);
            var nu = Nullable.GetUnderlyingType(t);
            if (nu != null) return r.ReadBoolean() ? ReadValue(r, nu, current, objs) : null;
            if (t.IsValueType) return ReadStruct(r, t, current, objs);
            int id = r.ReadInt32();
            if (id == -1) return null;
            if (id < -1) { int rid = -id - 2; return objs.TryGetValue(rid, out var known) ? known : current; } // skipped definition: keep live
            object target = current;
            string typeName = r.ReadString();
            if (target == null || target.GetType().FullName != typeName) {
                var nt = FindType(typeName);
                if (nt == null) throw new InvalidDataException("Unknown type in savestate: " + typeName);
                // A different live type (for example another RDP renderer) keeps the live object; the saved one is read
                // into a throwaway object so the stream stays in step.
                bool discard = target != null;
                object saved;
                if (nt.IsArray) saved = ReadArrayNew(r, nt, objs, id);
                else {
                    saved = RuntimeHelpers.GetUninitializedObject(nt);
                    objs[id] = saved;
                    s_Discard++;
                    try { ReadObjectBody(r, saved, objs); } finally { s_Discard--; }
                }
                if (discard) { objs[id] = target; return target; }
                return saved;
            }
            // [scripts_speed] An array that grew at run time (e.g. ParallelRdp.m_Pending for Perfect Dark's large RDP
            // batches) is read into a new array of the saved length instead of failing the load.
            if (target is Array live && s_Discard == 0) {
                long pos = r.BaseStream.Position; int savedLen = r.ReadInt32(); r.BaseStream.Position = pos;
                if (savedLen != live.Length) return ReadArrayNew(r, target.GetType(), objs, id);
            }
            objs[id] = target;
            ReadObjectBody(r, target, objs);
            return target;
        }

        private static void ReadObjectInto(BinaryReader r, object root, Dictionary<int, object> objs, int expectId)
        {
            int id = r.ReadInt32();
            if (id != expectId) throw new InvalidDataException("Savestate root mismatch");
            string typeName = r.ReadString();
            if (root.GetType().FullName != typeName) throw new InvalidDataException("Savestate root type mismatch");
            objs[id] = root;
            ReadObjectBody(r, root, objs);
        }

        private static object ReadStruct(BinaryReader r, Type t, object current, Dictionary<int, object> objs)
        {
            object boxed = current ?? Activator.CreateInstance(t);
            ReadFields(r, t, boxed, objs);
            return boxed;
        }

        private static unsafe void ReadObjectBody(BinaryReader r, object target, Dictionary<int, object> objs)
        {
            var t = target.GetType();
            if (target is UnmanagedBuffer ub) {
                bool owner = r.ReadBoolean();
                if (owner) {
                    int size = r.ReadInt32();
                    var bytes = r.ReadBytes(size);
                    if (s_Discard > 0 || ub.GetPointer() == IntPtr.Zero) return;
                    if (size != ub.Size) throw new InvalidDataException($"Native buffer size changed ({ub.Size} vs {size})");
                    bytes.AsSpan().CopyTo(new Span<byte>((void*)ub.GetPointer(), size));
                }
                return;
            }
            if (t.IsArray) {
                var a = (Array)target; var et = t.GetElementType();
                int len = r.ReadInt32();
                if (len != a.Length) {
                    if (s_Discard == 0) throw new InvalidDataException($"Array length changed for {t} ({a.Length} vs {len})");
                    a = Array.CreateInstance(et, len); // throwaway
                }
                if (et.IsPrimitive) { int n = r.ReadInt32(); var bytes = r.ReadBytes(n); Buffer.BlockCopy(bytes, 0, a, 0, n); }
                else if (SkipType(et)) { }
                else for (int i = 0; i < len; i++) a.SetValue(ReadValue(r, et, a.GetValue(i), objs), i);
                return;
            }
            ReadFields(r, t, target, objs);
        }

        private static void ReadFields(BinaryReader r, Type t, object target, Dictionary<int, object> objs)
        {
            var byName = new Dictionary<string, FieldInfo>();
            foreach (var f in Fields(t)) byName[f.Name] = f;
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++) {
                string name = r.ReadString();
                int len = r.ReadInt32();
                long end = r.BaseStream.Position + len;
                if (byName.TryGetValue(name, out var f)) {
                    var v = ReadValue(r, f.FieldType, f.GetValue(target), objs);
                    f.SetValue(target, v);
                }
                r.BaseStream.Position = end; // skips fields that no longer exist (and re-syncs on any mismatch)
            }
        }

        private static object ReadArrayNew(BinaryReader r, Type at, Dictionary<int, object> objs, int id)
        {
            var et = at.GetElementType();
            int len = r.ReadInt32();
            var a = Array.CreateInstance(et, len);
            objs[id] = a;
            if (et.IsPrimitive) { int n = r.ReadInt32(); var bytes = r.ReadBytes(n); Buffer.BlockCopy(bytes, 0, a, 0, n); }
            else if (SkipType(et)) { }
            else for (int i = 0; i < len; i++) a.SetValue(ReadValue(r, et, null, objs), i);
            return a;
        }

        public class SaveStatePathException : Exception {
            public SaveStatePathException(string path, Exception inner) : base("savestate failed at " + path + ": " + inner.Message, inner) { }
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }
    }
}
