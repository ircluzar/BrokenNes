using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Movies;

/// <summary>
/// A session's history (milestone D5): the input of every frame since the session started (the movie it is recording), a save state
/// every <see cref="Every"/> frames (the last <see cref="Keep"/> of them), and the picture and RAM hashes of its keyframes. It is what
/// step back is built on: put the machine back to the newest state before the place asked for, then replay the logged input up to it,
/// to the exact instruction (<see cref="Debugger.RunToInstructionCount"/>). Nothing of this runs unless a host creates one: the emulator
/// itself never knows.
///
/// <para>The host calls <see cref="BeforeFrame"/> at the start of each frame on the emulation thread (between frames: a state taken
/// there is a clean one) with the input it would apply live, and applies what it returns; then <see cref="AfterFrame"/>. While the
/// session is behind the end of its log (after a step back) the log's input is replayed, so running forward again gives the same
/// future; a new input given while behind rewrites the future from there (<see cref="Branch"/>).</para>
/// </summary>
public sealed class DebugHistory
{
    public sealed class Snapshot
    {
        public long Frame;          // taken at the start of this frame (frames run before it)
        public long Instructions;   // the main CPU's instruction count then (-1: no debugger)
        public long MachineFrame;   // the debugger's frame then
        public byte[] State = Array.Empty<byte>();
    }

    private readonly Func<byte[]> save;
    private readonly Debugger? debugger;
    private readonly List<(PadButtons P1, PadButtons P2)> log = new();
    private readonly List<Snapshot> snaps = new();
    private readonly List<MovieKey> keys = new();
    private readonly Func<(ulong picture, ulong ram)>? hashes;

    /// <summary>A state every this many frames.</summary>
    public int Every { get; }
    /// <summary>States kept (the oldest are dropped): Every x Keep frames of history.</summary>
    public int Keep { get; }
    /// <summary>Keyframes (picture and RAM hashes) every this many frames, for the movie; 0: none.</summary>
    public int KeyEvery { get; }
    /// <summary>The index of the next frame to start (frames run since the history began).</summary>
    public long FrameIndex { get; private set; }
    /// <summary>The frames whose input is logged (the movie's length so far).</summary>
    public long Logged => log.Count;
    public bool Replaying => FrameIndex < log.Count;
    public IReadOnlyList<Snapshot> Snapshots => snaps;
    public IReadOnlyList<MovieKey> Keys => keys;
    /// <summary>Why the history stopped taking states (the machine refused one), or null. Step back then reaches only the states before.</summary>
    public string? SaveError { get; private set; }
    /// <summary>Where the history began: null at power-on, else the state it began from.</summary>
    public byte[]? StartState { get; }

    public DebugHistory(Func<byte[]> save, Debugger? debugger = null, int every = 60, int keep = 30, int keyEvery = 60,
                        Func<(ulong picture, ulong ram)>? hashes = null, byte[]? startState = null)
    {
        this.save = save; this.debugger = debugger; this.hashes = hashes;
        Every = Math.Max(1, every); Keep = Math.Max(1, keep); KeyEvery = Math.Max(0, keyEvery);
        StartState = startState;
    }

    /// <summary>The start of a frame (emulation thread): takes the state when due, and gives the pads to apply (the log's while replaying).</summary>
    public (PadButtons P1, PadButtons P2) BeforeFrame(PadButtons live1, PadButtons live2)
    {
        long f = FrameIndex;
        if (f % Every == 0 && SaveError == null && !snaps.Any(s => s.Frame == f))
        {
            // a machine whose state cannot be taken (a chip without one) keeps running: the history stops taking states and says why
            byte[]? st = null;
            try { st = save(); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { SaveError = $"no states after frame {f}: {e.Message}"; }
            if (st != null)
            {
                snaps.Add(new Snapshot { Frame = f, Instructions = debugger != null ? debugger.InstructionCount() : -1, MachineFrame = debugger?.Machine.Frame ?? 0, State = st });
                snaps.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                while (snaps.Count > Keep) snaps.RemoveAt(0);
            }
        }
        if (f < log.Count) return log[(int)f];
        log.Add((live1, live2));
        return (live1, live2);
    }

    /// <summary>The end of a frame (emulation thread): the keyframe when due, and the check against a preloaded movie's keyframe.</summary>
    public void AfterFrame()
    {
        FrameIndex++;
        if (hashes == null) return;
        bool due = KeyEvery > 0 && FrameIndex % KeyEvery == 0 && !keys.Any(k => k.Frame == FrameIndex);
        MovieKey? want = null;
        if (expected != null && OffCourseFrame == null) expected.TryGetValue(FrameIndex, out want);
        if (!due && want == null) return;
        var (p, r) = hashes();
        if (due) keys.Add(new MovieKey { Frame = FrameIndex, Picture = p, Ram = r });
        if (want != null)
        {
            KeysChecked++;
            if (p == want.Picture && r == want.Ram) KeysMatched++;
            else { OffCourseFrame = FrameIndex; OffCourseWhat = (p != want.Picture ? "the picture" : "") + (p != want.Picture && r != want.Ram ? " and " : "") + (r != want.Ram ? "the work RAM" : ""); }
        }
    }

    private Dictionary<long, MovieKey>? expected;
    /// <summary>A preloaded movie's keyframes: checked, and the first that does not match.</summary>
    public int KeysChecked { get; private set; }
    public int KeysMatched { get; private set; }
    public long? OffCourseFrame { get; private set; }
    public string OffCourseWhat { get; private set; } = "";

    /// <summary>Plays a movie from here: its input becomes the log (replayed as the session runs, live input after its end) and its keyframes are checked.</summary>
    public void Preload(Movie m)
    {
        if (FrameIndex != 0 || log.Count != 0) throw new InvalidOperationException("a movie is preloaded before the first frame");
        for (long f = 0; f < m.Frames; f++) { var (a, b) = m.PadsAt(f); log.Add((a, b)); }
        expected = m.Keys.ToDictionary(k => k.Frame);
    }

    /// <summary>A new input while behind the end of the log: the future is rewritten from the current frame (its log, states and keys after it go).</summary>
    public void Branch()
    {
        if (FrameIndex >= log.Count) return;
        log.RemoveRange((int)FrameIndex, log.Count - (int)FrameIndex);
        snaps.RemoveAll(s => s.Frame > FrameIndex);
        keys.RemoveAll(k => k.Frame > FrameIndex);
    }

    /// <summary>The newest state at or before <paramref name="frame"/> (by frame), or null.</summary>
    public Snapshot? SnapshotAtOrBefore(long frame) => snaps.LastOrDefault(s => s.Frame <= frame);

    /// <summary>The newest state whose instruction count is at or before <paramref name="count"/>, or null.</summary>
    public Snapshot? SnapshotAtOrBeforeInstruction(long count) => snaps.LastOrDefault(s => s.Instructions >= 0 && s.Instructions <= count);

    /// <summary>After the host loaded <paramref name="s"/>: the next frame to start is the snapshot's (the debugger's count is put back too).</summary>
    public void Rewound(Snapshot s)
    {
        FrameIndex = s.Frame;
        if (debugger != null && s.Instructions >= 0) debugger.SetInstructionCount(s.Instructions);
        debugger?.Machine.RestoreFrame(s.MachineFrame);
    }

    /// <summary>The oldest frame the history can go back to.</summary>
    public long OldestFrame => snaps.Count > 0 ? snaps[0].Frame : FrameIndex;

    // ------------------------------------------------------------------------------------------------ step back

    /// <summary>What a step back did.</summary>
    public sealed class BackResult
    {
        public long FromFrame, ToFrame, FromInstruction = -1, ToInstruction = -1, SnapshotFrame, FramesReplayed;
        public RunOutcome? Outcome;
        public string Note = "";
    }

    /// <summary>
    /// Steps back <paramref name="n"/> instructions of the main CPU: the newest state before that point is loaded and the logged input
    /// replayed up to it, breakpoints and observers (the checks) off during the replay; the machine is then parked before that instruction.
    /// Needs the debugger's instruction count (its per-instruction hook armed since the history began: TrackCallStack on).
    /// <paramref name="ensureBetweenFrames"/> finishes a frame the machine is parked in (the host's own routine).
    /// </summary>
    public BackResult BackInstructions(DebugRunner runner, long n, Action<byte[]> load, Action ensureBetweenFrames)
    {
        var d = debugger ?? throw new InvalidOperationException("step back by instructions needs the debugger");
        if (!d.TrackCallStack) throw new InvalidOperationException("step back by instructions needs the instruction count: turn the call stack tracking on (it arms the per-instruction hook from the start)");
        if (n < 1) throw new ArgumentException("step back at least one instruction");
        long now = d.InstructionCount(), target = now - n;
        var s = SnapshotAtOrBeforeInstruction(target)
            ?? throw new InvalidOperationException($"the history does not reach back {n} instructions (its oldest state is at frame {OldestFrame}, instruction {(snaps.Count > 0 ? snaps[0].Instructions : now)}; {Keep} states every {Every} frames are kept)");
        var r = new BackResult { FromFrame = FrameIndex, FromInstruction = now, ToInstruction = target, SnapshotFrame = s.Frame };
        ensureBetweenFrames();
        long before = FrameIndex;
        Replay(d, s, load, () =>
        {
            if (target > s.Instructions) r.Outcome = runner.Step(() => d.RunToInstructionCount(target), guardFrames: Math.Max(2, before - s.Frame + 2));
        });
        r.ToFrame = FrameIndex; r.FramesReplayed = FrameIndex - s.Frame;
        if (target > s.Instructions && (r.Outcome != RunOutcome.Break || !d.IsParked)) r.Note = $"the replay did not stop at instruction {target} ({r.Outcome}): the run is not deterministic from the state at frame {s.Frame}";
        else if (d.InstructionCount() != target) r.Note = $"the replay stopped at instruction {d.InstructionCount()}, not {target}";
        return r;
    }

    /// <summary>
    /// Steps back to the start of the <paramref name="n"/>-th frame before now (parked inside frame F: n = 1 is the start of F; between
    /// frames before F: n = 1 is the start of F - 1). Ends between frames, not parked.
    /// </summary>
    public BackResult BackFrames(DebugRunner? runner, long n, bool parked, Action<byte[]> load, Action ensureBetweenFrames, Action<long>? runPlain = null)
    {
        if (n < 1) throw new ArgumentException("step back at least one frame");
        long target = (parked ? FrameIndex + 1 : FrameIndex) - n;
        if (target < 0) throw new InvalidOperationException($"there are only {FrameIndex} frames before now");
        var s = SnapshotAtOrBefore(target)
            ?? throw new InvalidOperationException($"the history does not reach back to frame {target} (its oldest state is at frame {OldestFrame}; {Keep} states every {Every} frames are kept)");
        var r = new BackResult { FromFrame = FrameIndex, SnapshotFrame = s.Frame, FromInstruction = debugger?.InstructionCount() ?? -1 };
        ensureBetweenFrames();
        Replay(debugger, s, load, () =>
        {
            long k = target - s.Frame;
            if (k <= 0) return;
            if (runner != null) r.Outcome = runner.Run(k);
            else (runPlain ?? throw new InvalidOperationException("no way to run frames"))(k);
        });
        r.ToFrame = FrameIndex; r.FramesReplayed = FrameIndex - s.Frame; r.ToInstruction = debugger?.InstructionCount() ?? -1;
        if (FrameIndex != target) r.Note = $"the replay ended at frame {FrameIndex}, not {target}";
        return r;
    }

    private void Replay(Debugger? d, Snapshot s, Action<byte[]> load, Action run)
    {
        var on = d?.Breakpoints.Where(b => b.Enabled).Select(b => b.Id).ToList() ?? new List<int>();
        var obs = d?.Observers.ToArray() ?? Array.Empty<DebugObserver>();
        if (d != null) { foreach (var id in on) d.SetEnabled(id, false); foreach (var o in obs) d.RemoveObserver(o); }
        try
        {
            load(s.State);
            Rewound(s);
            if (d != null && d.TrackCallStack) { d.TrackCallStack = false; d.TrackCallStack = true; d.SetInstructionCount(s.Instructions); }   // the call stack starts again at the state
            run();
        }
        finally
        {
            if (d != null) { foreach (var o in obs) d.AddObserver(o); foreach (var id in on) d.SetEnabled(id, true); }
        }
    }

    /// <summary>The logged input as a movie (the frames logged so far; the caller fills in the ROM, cores and the rest).</summary>
    public Movie ToMovie(long? upTo = null)
    {
        long n = Math.Min(log.Count, upTo ?? log.Count);
        if (n <= 0) throw new InvalidOperationException("no frame has run yet: a movie needs at least one");
        var m = new Movie { Frames = n, KeyEvery = KeyEvery, Start = StartState != null ? MovieStart.State : MovieStart.PowerOn, StartState = StartState };
        for (int i = 0; i < n; i++) m.SetPads(i, log[i].P1, log[i].P2);
        if (m.Inputs.Count == 0) m.SetPads(0, PadButtons.None, PadButtons.None);
        foreach (var k in keys.Where(k => k.Frame <= n)) m.Keys.Add(new MovieKey { Frame = k.Frame, Picture = k.Picture, Ram = k.Ram });
        return m;
    }
}
