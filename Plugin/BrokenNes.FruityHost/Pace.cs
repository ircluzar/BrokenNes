using System.Diagnostics;

namespace BrokenNes.FruityHost;

/// <summary>
/// How long a stretch of work took, for the "x real time" figures. <see cref="Seconds"/> is the processor time the host process used (not the wall clock). That is NOT immune to other programs sharing the machine
/// (they slow every instruction: see <see cref="BlockClock"/>, which the `realtime` check uses for its verdicts): the floor the plugin promises (5x real time with four instances) is a statement about how much of a core it needs. The wall clock is kept beside it (<see cref="Wall"/>) and printed with every figure, so a
/// reader can see how busy the machine was; on a quiet machine the two agree.
/// </summary>
public sealed class Pace
{
    private readonly Stopwatch sw = Stopwatch.StartNew();
    private readonly TimeSpan cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
    private double wall = -1, cpu = -1;

    public static Pace Start() => new();

    public void Stop()
    {
        wall = sw.Elapsed.TotalSeconds;
        cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds;
    }

    /// <summary>The wall-clock seconds of the stretch.</summary>
    public double Wall => wall >= 0 ? wall : sw.Elapsed.TotalSeconds;

    /// <summary>The processor seconds the process used during the stretch (never reported as less than a thousandth of a second).</summary>
    public double Seconds => Math.Max(1e-3, cpu >= 0 ? cpu : (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds);

    /// <summary>"7.9x (6.2x on the wall clock)" for <paramref name="audioSeconds"/> of sound.</summary>
    public string Describe(double audioSeconds) => $"{audioSeconds / Seconds:0.0}x real time (processor time; {audioSeconds / Math.Max(1e-3, Wall):0.0}x on the wall clock)";
}

/// <summary>
/// The pace of a stretch of audio blocks, robust to a busy machine. <see cref="Pace"/>'s processor time is NOT immune to other programs: the same fixed
/// integer loop cost 0.6 s of processor time with the machine as it was and 1.4-1.8 s with twelve more busy threads beside it (2026-11-09, 6 cores /
/// 12 threads: shared caches, memory bandwidth, the sibling hyperthread and the turbo clock), and the same plugin DLL measured 3.1x and 2.2x in two
/// consecutive `realtime` runs. So the time of every block is recorded (a timestamp into a preallocated array: nothing allocates), the blocks are cut
/// into windows of equal work, and the pace is the median of the fastest quarter of the windows: the plugin at its own speed, in the moments the
/// machine let it run. This is not a softer floor: the work of every window is the same, so a plugin too slow for the floor is too slow in every one.
/// </summary>
public sealed class BlockClock
{
    private readonly long[] stamps;
    private int count;
    public BlockClock(int blocks) { stamps = new long[blocks + 1]; }
    /// <summary>Call at the boundary before block 0 and after every block.</summary>
    public void Mark() { if (count < stamps.Length) stamps[count++] = Stopwatch.GetTimestamp(); }
    public int Marks => count;

    /// <summary>The audio seconds per wall second over blocks [<paramref name="from"/>, <paramref name="to"/>), in windows of <paramref name="perWindow"/> blocks
    /// of <paramref name="blockSeconds"/> each: the median of the fastest quarter of the windows. <paramref name="spread"/> = slowest window's pace, for the report.</summary>
    public double Speed(int from, int to, int perWindow, double blockSeconds, out double whole, out double slowest)
    {
        to = Math.Min(to, count - 1);
        var w = new List<double>();
        for (int b = from; b + perWindow <= to; b += perWindow) w.Add((stamps[b + perWindow] - stamps[b]) / (double)Stopwatch.Frequency);
        if (w.Count == 0) { whole = slowest = 0; return 0; }
        double audio = perWindow * blockSeconds;
        whole = (to - from) * blockSeconds / ((stamps[to] - stamps[from]) / (double)Stopwatch.Frequency);
        w.Sort();
        slowest = audio / w[^1];
        int q = Math.Max(1, w.Count / 4);
        return audio / w[q / 2];
    }
}
