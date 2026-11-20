// [cer64plus] Load-proof benchmark metric. Wall time on a shared machine includes time other processes took from us,
// so A/B decisions also look at the cycles the emulator thread itself executed (QueryThreadCycleTime counts only while
// the thread is on a CPU), the whole process's cycles, the time the main thread was runnable but not running
// (wall - thread CPU), and system load during the run. All user mode, no admin.
using System;
using System.Runtime.InteropServices;

static class LoadProofMetric
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [DllImport("kernel32.dll")] private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr t, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public struct Mark { public ulong ThreadCyc, ProcCyc; public long ThreadCpu100ns, SysIdle, SysTotal, Ts; }

    public static Mark Take()
    {
        var m = new Mark();
        QueryThreadCycleTime(GetCurrentThread(), out m.ThreadCyc);
        QueryProcessCycleTime(GetCurrentProcess(), out m.ProcCyc);
        GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u); m.ThreadCpu100ns = k + u;
        GetSystemTimes(out long idle, out long sk, out long su); m.SysIdle = idle; m.SysTotal = sk + su; // kernel includes idle
        m.Ts = System.Diagnostics.Stopwatch.GetTimestamp();
        return m;
    }

    // One line per run. mainMcycPerFrame is the decision metric for single-thread work; wall for anything with threads.
    public static string Report(Mark a, Mark b, int frames)
    {
        double wallMs = (b.Ts - a.Ts) * 1e3 / System.Diagnostics.Stopwatch.Frequency;
        double cpuMs = (b.ThreadCpu100ns - a.ThreadCpu100ns) / 1e4;
        double sysTot = b.SysTotal - a.SysTotal, sysIdle = b.SysIdle - a.SysIdle;
        double load = sysTot > 0 ? 100.0 * (1 - sysIdle / sysTot) : 0;
        return $"METRIC mainMcycPerFrame={(b.ThreadCyc - a.ThreadCyc) / 1e6 / frames:F3} procMcycPerFrame={(b.ProcCyc - a.ProcCyc) / 1e6 / frames:F3} " +
               $"wallMsPerFrame={wallMs / frames:F3} mainCpuMsPerFrame={cpuMs / frames:F3} preemptPct={(wallMs > 0 ? 100 * (1 - cpuMs / wallMs) : 0):F1} sysLoadPct={load:F0}";
    }
}
