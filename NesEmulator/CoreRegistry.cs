using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NesEmulator
{
    // Central registry for discoverable emulator cores.
    // Discovers concrete types implementing ICPU/IPPU/IAPU whose names follow PREFIX_SUFFIX (e.g. CPU_FMC, APU_FIX).
    public static class CoreRegistry
    {
        private static bool _initialized;
        private static readonly object _lock = new();
        private static readonly List<string> _cpuIds = new();
        private static readonly List<string> _ppuIds = new();
        private static readonly List<string> _apuIds = new();
    // Cached type maps (suffix -> Type) to avoid repeated assembly scans and enable lazy instantiation
    private static readonly Dictionary<string, Type> _cpuTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Type> _ppuTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Type> _apuTypes = new(StringComparer.OrdinalIgnoreCase);
        private static Assembly? _assembly;

        public static void Initialize()
        {
            if (_initialized) return;
            lock(_lock)
            {
                if (_initialized) return;
                _assembly = typeof(NES).Assembly;
                Scan();
                _initialized = true;
            }
        }

        private static void Scan()
        {
            if (_assembly == null) return;
            TryFillListsAndTypes(_cpuIds, _cpuTypes, typeof(ICPU), "CPU_");
            TryFillListsAndTypes(_ppuIds, _ppuTypes, typeof(IPPU), "PPU_");
            TryFillListsAndTypes(_apuIds, _apuTypes, typeof(IAPU), "APU_");
        }

        private static void TryFillListsAndTypes(List<string> idList, Dictionary<string, Type> typeMap, Type iface, string prefix)
        {
            idList.Clear();
            typeMap.Clear();
            try
            {
                var pairs = _assembly!.GetTypes()
                    .Where(t => iface.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface && t.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(t => (Id: t.Name.Substring(prefix.Length), Type: t))
                    .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var p in pairs)
                {
                    idList.Add(p.Id);
                    typeMap[p.Id] = p.Type;
                }
            }
            catch { }
        }

        public static IReadOnlyList<string> CpuIds { get { Initialize(); return _cpuIds; } }
        public static IReadOnlyList<string> PpuIds { get { Initialize(); return _ppuIds; } }
        public static IReadOnlyList<string> ApuIds { get { Initialize(); return _apuIds; } }

        public static string ExtractSuffix(string typeName, string prefix)
        {
            if (typeName.StartsWith(prefix, StringComparison.Ordinal) && typeName.Length > prefix.Length)
                return typeName.Substring(prefix.Length);
            var idx = typeName.IndexOf('_');
            return idx > 0 && idx < typeName.Length - 1 ? typeName[(idx+1)..] : typeName;
        }

        // Expose cached type maps for lazy creation
        public static IReadOnlyDictionary<string, Type> CpuTypes { get { Initialize(); return _cpuTypes; } }
        public static IReadOnlyDictionary<string, Type> PpuTypes { get { Initialize(); return _ppuTypes; } }
        public static IReadOnlyDictionary<string, Type> ApuTypes { get { Initialize(); return _apuTypes; } }

        public static IReadOnlyDictionary<string, Type> GetCoreTypes<TIface>(string prefix)
        {
            Initialize();
            if (typeof(TIface) == typeof(ICPU)) return CpuTypes;
            if (typeof(TIface) == typeof(IPPU)) return PpuTypes;
            if (typeof(TIface) == typeof(IAPU)) return ApuTypes;
            // Fallback: scan for arbitrary interface type (rare path)
            var dict = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            if (_assembly == null) return dict;
            try
            {
                foreach (var t in _assembly.GetTypes())
                {
                    if (!typeof(TIface).IsAssignableFrom(t) || t.IsAbstract || t.IsInterface) continue;
                    if (!t.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var id = ExtractSuffix(t.Name, prefix);
                    if (!dict.ContainsKey(id)) dict[id] = t;
                }
            }
            catch { }
            return dict;
        }

        // Helper factory: create a core instance from Type using (Bus) or default ctor
        public static TIface? CreateInstance<TIface>(Type t, Bus bus) where TIface : class
        {
            Initialize();
            try
            {
                object? instance = null;
                var ctorBus = t.GetConstructor(new[] { typeof(Bus) });
                if (ctorBus != null)
                    instance = ctorBus.Invoke(new object[] { bus });
                else
                {
                    var ctorDefault = t.GetConstructor(Type.EmptyTypes);
                    if (ctorDefault != null)
                        instance = ctorDefault.Invoke(null);
                }
                return instance as TIface;
            }
            catch { return null; }
        }

        // Create (or recreate) instances of all discovered core implementations for a given interface.
        // Tries to invoke a (Bus) constructor first; falls back to parameterless if present.
        public static Dictionary<string, TIface> CreateInstances<TIface>(Bus bus, string prefix) where TIface : class
        {
            Initialize();
            var dict = new Dictionary<string, TIface>(StringComparer.OrdinalIgnoreCase);
            if (_assembly == null) return dict;
            foreach (var t in _assembly.GetTypes())
            {
                if (!typeof(TIface).IsAssignableFrom(t) || t.IsAbstract || t.IsInterface) continue;
                if (!t.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                try
                {
                    object? instance = null;
                    // Prefer (Bus) constructor to let cores access the bus
                    var ctorBus = t.GetConstructor(new[] { typeof(Bus) });
                    if (ctorBus != null)
                        instance = ctorBus.Invoke(new object[] { bus });
                    else
                    {
                        var ctorDefault = t.GetConstructor(Type.EmptyTypes);
                        if (ctorDefault != null)
                            instance = ctorDefault.Invoke(null);
                    }
                    if (instance is TIface core)
                    {
                        var id = ExtractSuffix(t.Name, prefix);
                        if (!dict.ContainsKey(id))
                            dict[id] = core;
                    }
                }
                catch
                {
                    // swallow – unsafe / experimental core types shouldn't break registry
                }
            }
            return dict;
        }

        // Lightweight verification helper to assert discovery and ordering in debug builds.
        // Returns true when CPU/PPU/APU suffix ids are unique and sorted case-insensitively.
    public static bool VerifyIdsAndTypes()
        {
            Initialize();
            bool ok = true;
        static bool IsSortedUnique(List<string> list)
            {
                for (int i = 1; i < list.Count; i++)
                {
            if (string.Compare(list[i - 1], list[i], StringComparison.OrdinalIgnoreCase) > 0) return false;
            if (string.Equals(list[i - 1], list[i], StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }
            ok &= IsSortedUnique(_cpuIds);
            ok &= IsSortedUnique(_ppuIds);
            ok &= IsSortedUnique(_apuIds);
            // Type maps must contain same keys
            ok &= _cpuIds.All(id => _cpuTypes.ContainsKey(id));
            ok &= _ppuIds.All(id => _ppuTypes.ContainsKey(id));
            ok &= _apuIds.All(id => _apuTypes.ContainsKey(id));
            return ok;
        }
    }
}
