// [climb4 speed_cat17] measurement only: COR64_SPDCAT17_PROF=<file> samples the CPU thread's instruction pointer (suspend +
// GetThreadContext, ~every 200 us) and writes, at process exit, the native blocks by sample count plus the code bytes and
// per-offset counts of the hottest blocks (for an offline disassembler). Nothing in the emulation reads it.
// HANG TRAP (2026-11-20, Racer/ASB99): a GC during sampling can deadlock (sampler stuck in GetThreadContext on the
// suspended CPU thread while the GC waits for it). Run with DOTNET_GCgen0size=0x30000000 (no gen0 GC in a 5400-frame bench).
// Report also buckets the samples outside native blocks by module (managed JIT = "jit/heap") and lists the top 600 RIPs
// (resolve with dotnet-dump ip2md on a dump taken from the same run).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace cor64.Mips.R4300I
{
    internal static unsafe class X64Prof
    {
        public static readonly string Path = Environment.GetEnvironmentVariable("COR64_SPDCAT17_PROF");
        public static readonly bool On = !string.IsNullOrEmpty(Path) && OperatingSystem.IsWindows();
        private static readonly List<(long start, int len, CpuRecBlock b)> s_Blocks = new();
        private static readonly Dictionary<long, int> s_Rip = new();
        private static long s_Total, s_Fail;
        private static bool s_Started;

        [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern bool GetModuleHandleExW(uint flags, IntPtr addr, out IntPtr mod);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileNameW(IntPtr mod, StringBuilder name, uint size);
        [DllImport("kernel32")] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32"), System.Runtime.InteropServices.SuppressGCTransition] private static extern uint SuspendThread(IntPtr h);
        [DllImport("kernel32"), System.Runtime.InteropServices.SuppressGCTransition] private static extern uint ResumeThread(IntPtr h);
        [DllImport("kernel32"), System.Runtime.InteropServices.SuppressGCTransition] private static extern bool GetThreadContext(IntPtr h, IntPtr ctx);

        public static void Add(long start, int len, CpuRecBlock b) { lock (s_Blocks) s_Blocks.Add((start, len, b)); }

        public static void Start()
        {
            if (s_Started) return;
            s_Started = true;
            uint tid = GetCurrentThreadId();
            var t = new System.Threading.Thread(() => Run(tid)) { IsBackground = true, Priority = System.Threading.ThreadPriority.Highest };
            t.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Report();
        }

        private static void Run(uint tid)
        {
            IntPtr h = OpenThread(0x0002 | 0x0008 | 0x0010 | 0x0040, false, tid); // suspend/resume, get context, query
            IntPtr raw = Marshal.AllocHGlobal(1232 + 16);
            IntPtr ctx = (IntPtr)(((long)raw + 15) & ~15L);
            while (true) {
                System.Threading.Thread.Sleep(0);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedTicks < System.Diagnostics.Stopwatch.Frequency / 5000) System.Threading.Thread.SpinWait(50);
                if (SuspendThread(h) == uint.MaxValue) { s_Fail++; continue; }
                *(uint*)((byte*)ctx + 0x30) = 0x100001; // CONTEXT_CONTROL
                long rip = GetThreadContext(h, ctx) ? *(long*)((byte*)ctx + 0xF8) : 0;
                ResumeThread(h);
                lock (s_Rip) { s_Rip.TryGetValue(rip, out int c); s_Rip[rip] = c + 1; s_Total++; }
            }
        }

        private static void Report()
        {
            try {
                (long start, int len, CpuRecBlock b)[] bl; lock (s_Blocks) bl = s_Blocks.ToArray();
                Array.Sort(bl, (x, y) => x.start.CompareTo(y.start));
                KeyValuePair<long, int>[] rips; lock (s_Rip) rips = new List<KeyValuePair<long, int>>(s_Rip).ToArray();
                var other = new List<KeyValuePair<long, int>>(); var per = new Dictionary<int, long>(); var off = new Dictionary<int, Dictionary<int, int>>(); long nat = 0;
                foreach (var kv in rips) {
                    int lo = 0, hi = bl.Length - 1, f = -1;
                    while (lo <= hi) { int m = (lo + hi) / 2; if (bl[m].start <= kv.Key) { f = m; lo = m + 1; } else hi = m - 1; }
                    if (f < 0 || kv.Key >= bl[f].start + bl[f].len) { other.Add(kv); continue; }
                    nat += kv.Value;
                    per.TryGetValue(f, out long c); per[f] = c + kv.Value;
                    if (!off.TryGetValue(f, out var d)) off[f] = d = new();
                    d[(int)(kv.Key - bl[f].start)] = kv.Value;
                }
                var sb = new StringBuilder();
                sb.AppendLine($"PROF total={s_Total} native={nat} fail={s_Fail}");
                // samples outside the native blocks: by module (managed JIT code has no module: "jit/heap"), then the hottest raw RIPs
                var mods = new Dictionary<string, long>(); var modBase = new Dictionary<long, string>();
                foreach (var kv in other) {
                    string m = "jit/heap";
                    if (GetModuleHandleExW(6, (IntPtr)kv.Key, out IntPtr mh) && mh != IntPtr.Zero) { var nb = new StringBuilder(260); GetModuleFileNameW(mh, nb, 260); m = System.IO.Path.GetFileName(nb.ToString()) + "@" + ((long)mh).ToString("X"); }
                    mods.TryGetValue(m, out long c); mods[m] = c + kv.Value;
                }
                foreach (var kv in mods) sb.AppendLine($"MOD {kv.Key} samples={kv.Value} pct={100.0 * kv.Value / Math.Max(1, s_Total):F2}");
                other.Sort((x, y) => y.Value.CompareTo(x.Value));
                for (int i = 0; i < Math.Min(600, other.Count); i++) sb.AppendLine($"RIP {other[i].Key:X} samples={other[i].Value}");
                var order = new List<int>(per.Keys); order.Sort((x, y) => per[y].CompareTo(per[x]));
                for (int i = 0; i < Math.Min(60, order.Count); i++) {
                    var b = bl[order[i]].b;
                    sb.Append($"BLK {(uint)b.StartPc:X8} n={b.Insts.Length} samples={per[order[i]]} pct={100.0 * per[order[i]] / Math.Max(1, s_Total):F2} :");
                    foreach (var d in b.Insts) sb.Append(' ').Append(d.Op.Op.Trim());
                    sb.AppendLine();
                }
                for (int i = 0; i < Math.Min(12, order.Count); i++) {
                    var e = bl[order[i]];
                    sb.Append($"CODE {(uint)e.b.StartPc:X8} ");
                    for (int k = 0; k < e.len; k++) sb.Append(((byte*)e.start)[k].ToString("x2"));
                    sb.AppendLine();
                    sb.Append($"OFFS {(uint)e.b.StartPc:X8}");
                    foreach (var kv in off[order[i]]) sb.Append($" {kv.Key}:{kv.Value}");
                    sb.AppendLine();
                }
                System.IO.File.WriteAllText(Path, sb.ToString());
            }
            catch (Exception ex) { System.IO.File.WriteAllText(Path, "PROF error " + ex); }
        }
    }
}
