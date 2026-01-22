using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NesEmulator
{
    // Registry for discoverable Clock Cores (types named CLOCK_* implementing IClock)
    public static class ClockRegistry
    {
        private static bool _initialized;
        private static readonly object _lock = new();
        private static readonly List<string> _ids = new();
        private static readonly Dictionary<string, Type> _types = new(StringComparer.OrdinalIgnoreCase);
        private static Assembly? _assembly;

        public static void Initialize()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                _assembly = typeof(NES).Assembly;
                Scan();
                _initialized = true;
            }
        }

        private static void Scan()
        {
            _ids.Clear(); _types.Clear();
            if (_assembly == null) return;
            try
            {
                var pairs = _assembly.GetTypes()
                    .Where(t => typeof(IClock).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface && t.Name.StartsWith("CLOCK_", StringComparison.Ordinal))
                    .Select(t => (Id: CoreRegistry.ExtractSuffix(t.Name, "CLOCK_"), Type: t))
                    .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var p in pairs)
                {
                    _ids.Add(p.Id);
                    _types[p.Id] = p.Type;
                }
            }
            catch { }
        }

        public static IReadOnlyList<string> Ids { get { Initialize(); return _ids; } }
        public static IReadOnlyDictionary<string, Type> Types { get { Initialize(); return _types; } }

        public static IClock? Create(string id)
        {
            Initialize();
            if (!_types.TryGetValue(id, out var t)) return null;
            try
            {
                var ctor = t.GetConstructor(Type.EmptyTypes);
                if (ctor != null) return ctor.Invoke(null) as IClock;
            }
            catch { }
            return null;
        }
    }
}
