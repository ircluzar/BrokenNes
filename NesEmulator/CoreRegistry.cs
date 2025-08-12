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
            TryFill(_cpuIds, typeof(ICPU), "CPU_");
            TryFill(_ppuIds, typeof(IPPU), "PPU_");
            TryFill(_apuIds, typeof(IAPU), "APU_");
        }

        private static void TryFill(List<string> target, Type iface, string prefix)
        {
            target.Clear();
            try
            {
                var ids = _assembly!.GetTypes()
                    .Where(t => iface.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface && t.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(t => t.Name.Substring(prefix.Length))
                    .Distinct()
                    .OrderBy(s => s)
                    .ToList();
                target.AddRange(ids);
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
    }
}
