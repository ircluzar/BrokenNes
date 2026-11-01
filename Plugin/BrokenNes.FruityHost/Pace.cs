using System.Diagnostics;

namespace BrokenNes.FruityHost;

/// <summary>
/// How long a stretch of work took, for the "x real time" figures. <see cref="Seconds"/> is the processor time the host process used (not the wall clock), so a figure is not skewed by other programs sharing
/// the machine: the floor the plugin promises (5x real time with four instances) is a statement about how much of a core it needs. The wall clock is kept beside it (<see cref="Wall"/>) and printed with every figure, so a
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
