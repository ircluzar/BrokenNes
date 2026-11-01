// Round 9 (capture): one take: "Capture from FL" arms it (every BrokenNes2 instance of the process gets a lane), FL's Play starts it (each instance's own FPD_SetPlaying starts its lane's clock),
// FL's Stop ends it, and a worker builds the MIDI file (CaptureSong) and hands it over (OnFinished: the plugin feeds it to Test MIDI export). Pure code: no plugin, no UI.
//
//   Armed      lanes exist (their first chunks made on the UI thread), nothing recorded yet.
//   Recording  from the first lane FL started; a lane FL never starts that gets a note joins at the others' clock (said in the facts).
//   Finishing  from the first lane FL stopped; the worker waits until every started lane stopped (or 400 ms), then builds.
//   Done       the result is there (a file, or the reason there is none).
//   Cancelled  the user cancelled (nothing is built).
namespace BrokenNes2.Capture;

public sealed class CaptureSession
{
    public const int Armed = 1, Recording = 2, Finishing = 3, Done = 4, Cancelled = 0;
    private const int PollMs = 20, StopGraceMs = 400;

    private static long nextId;
    private static volatile CaptureSession? current;

    /// <summary>The take armed, running or last finished (null: none since the process started).</summary>
    public static CaptureSession? Current => current;

    public readonly long Id = Interlocked.Increment(ref nextId);
    public readonly string Name;
    public readonly DateTime ArmedAt = DateTime.Now;
    private readonly List<CaptureLane> lanes = new();
    private readonly object gate = new();
    private int state = Armed;
    private long stopTick;
    private readonly Func<FlTiming> timing;
    /// <summary>What FL had said about time when the take started and when it stopped.</summary>
    public FlTiming StartTiming { get; private set; }
    public FlTiming EndTiming { get; private set; }
    public CaptureResult? Result { get; private set; }
    /// <summary>Called on the worker once the result is there (the plugin feeds it to Test MIDI export).</summary>
    public Action<CaptureSession>? OnFinished;
    /// <summary>Called on the worker with the lanes' snapshots, just before building (the plugin refreshes the lanes' names: an instance may have changed channel since it was armed).</summary>
    public Func<LaneData, LaneIdentity>? RefreshIdentity;
    private Thread? worker;

    private CaptureSession(string name, Func<FlTiming> timing) { Name = name; this.timing = timing; }

    /// <summary>Arms a new take (the UI thread): the one before is cancelled if it still runs.</summary>
    public static CaptureSession Arm(string name, Func<FlTiming> timing)
    {
        current?.Cancel();
        var s = new CaptureSession(name, timing);
        current = s;
        return s;
    }

    /// <summary>Tests: no take (as in a fresh process).</summary>
    public static void Forget() { current?.Cancel(); current = null; }

    public int State => Volatile.Read(ref state);
    public bool Listening => State is Armed or Recording;

    /// <summary>A lane for an instance (the UI thread, or the constructor of an instance made while armed): its first chunks are made here.</summary>
    public CaptureLane NewLane(LaneIdentity id)
    {
        var l = new CaptureLane(id);
        lock (gate) lanes.Add(l);
        EnsureWorker();
        return l;
    }

    public int LaneCount { get { lock (gate) return State == Done ? laneCountAtEnd : lanes.Count; } }
    private int laneCountAtEnd;
    public CaptureLane[] Lanes { get { lock (gate) return lanes.ToArray(); } }

    /// <summary>FL started its transport on an instance (FPD_SetPlaying 1; any thread, nothing allocated).</summary>
    public void TransportStart(CaptureLane lane)
    {
        int s = State;
        if (s is not (Armed or Recording)) return;
        if (s == Armed && Interlocked.CompareExchange(ref state, Recording, Armed) == Armed) StartTiming = timing();
        lane.Start();
    }

    /// <summary>FL stopped its transport on an instance (FPD_SetPlaying 0).</summary>
    public void TransportStop(CaptureLane lane)
    {
        if (State is not (Recording or Finishing)) return;
        lane.Stop();
        if (Interlocked.CompareExchange(ref state, Finishing, Recording) == Recording) { EndTiming = timing(); Volatile.Write(ref stopTick, Environment.TickCount64); }
    }

    /// <summary>A note on a lane FL never started while the take runs: it joins at the others' clock (said). Allocation-free.</summary>
    public void JoinLate(CaptureLane lane)
    {
        if (State != Recording || lane.State != 0) return;
        double at = 0;
        lock (gate) for (int i = 0; i < lanes.Count; i++) if (lanes[i] != lane && lanes[i].Running) at = Math.Max(at, lanes[i].Seconds);
        lane.Start(at, late: true);
    }

    /// <summary>The take's clock now (the longest-running lane's), for the window.</summary>
    public double Seconds { get { double s = 0; foreach (var l in Lanes) if (l.State != 0) s = Math.Max(s, l.Seconds); return s; } }
    public int EventCount { get { int n = 0; foreach (var l in Lanes) n += l.Count; return n; } }

    public void Cancel()
    {
        int s = State;
        if (s is Done or Cancelled) return;
        Volatile.Write(ref state, Cancelled);
    }

    private void EnsureWorker()
    {
        lock (gate)
        {
            if (worker != null) return;
            worker = new Thread(Loop) { IsBackground = true, Name = "BrokenNes2 capture" };
            worker.Start();
        }
    }

    private void Loop()
    {
        while (true)
        {
            Thread.Sleep(PollMs);
            int s = State;
            if (s is Cancelled or Done) return;
            foreach (var l in Lanes) l.Feed();
            if (s != Finishing) continue;
            var started = Lanes.Where(l => l.State != 0).ToArray();
            bool all = started.All(l => l.State == 2);
            if (!all && Environment.TickCount64 - Volatile.Read(ref stopTick) < StopGraceMs) continue;
            foreach (var l in started) l.Stop();
            Finish();
            return;
        }
    }

    /// <summary>Builds the result (the worker; tests may call it directly once every lane stopped).</summary>
    public void Finish()
    {
        if (State == Done) return;
        var data = Lanes.Select(l => l.Snapshot()).ToList();
        if (RefreshIdentity != null) foreach (var d in data) { try { d.Identity = RefreshIdentity(d); } catch (Exception) { /* keep the identity it was armed with */ } }
        CaptureResult r;
        try { r = CaptureSong.Build(Name, UniqueNames(data), StartTiming, EndTiming.Rate == 0 ? timing() : EndTiming); }
        catch (Exception e) { r = new CaptureResult { Name = Name, Problem = "the capture could not be turned into a MIDI file: " + e.Message }; r.Report.Add(e.ToString()); }
        Result = r;
        laneCountAtEnd = data.Count;
        lock (gate) lanes.Clear();                                                    // (the lanes' memory goes: the result holds what was captured)
        Volatile.Write(ref state, Done);
        try { OnFinished?.Invoke(this); } catch (Exception) { /* the window shows the result whatever the handler did */ }
    }

    /// <summary>Two instances of one role (two Pulse 1 on two emulators): their tracks get distinct names.</summary>
    private static List<LaneData> UniqueNames(List<LaneData> data)
    {
        foreach (var g in data.GroupBy(d => d.Identity.Name).Where(g => g.Count() > 1))
            foreach (var d in g.Where(d => d.Count > 0 || d.Started))
                d.Identity = d.Identity with { Name = $"{d.Identity.Name} (#{d.Identity.InstanceNo})" };
        return data;
    }
}
