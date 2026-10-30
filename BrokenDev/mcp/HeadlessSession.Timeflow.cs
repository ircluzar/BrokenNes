using System.Text.Json.Nodes;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Timeflow;
using NesEmulator.Systems;

namespace BrokenNes.Mcp;

/// <summary>
/// Timeflow on a headless session (milestones T1 and T2 of docs/projects/brokendev-timeflow-plan.md): the session's own recording switch
/// (an agent's own machine, never the user's play: the user's desktop app only records when the user switches it on or accepts a request),
/// a jump to any frame of a timeline of the same ROM, and the resume (the recorded inputs, checked, or live input as a branch).
/// </summary>
internal sealed partial class HeadlessSession
{
    private readonly TimeflowSwitch tf = new();
    private byte[] romFile = Array.Empty<byte>();
    private string romFileName = "";
    private string? tfBranchParent;
    private long tfBranchFrame;
    // T4: the search index (the frame's coverage and access counts) the session's recordings carry once asked for (timeflow_record start index:true)
    private FrameCoverage? tfCoverage;
    private FrameAccesses? tfAccesses;

    /// <summary>The session's recordings from now on carry the search index (coverage and per-page access counts): needs the debugger.</summary>
    public void TimeflowIndexOn()
    {
        var d = D ?? throw new ToolError($"the search index (coverage, access counts) needs the debugger, which does not cover the {Consoles.DisplayName(console)} yet (T0 for the Sega consoles): record without index");
        EnsureBetweenFrames();
        if (tfCoverage == null) { tfCoverage = FrameCoverage.Attach(d); tfCoverage.TakeFrame(new()); }
        if (tfAccesses == null) { tfAccesses = FrameAccesses.Attach(d); tfAccesses.TakeFrame(); }
    }
    // the last jump: the timeline and frame the session was put at, for resume (replay)
    private string? jumpedDir;
    private long jumpedFrame, jumpedAtSessionFrame;

    private TimeflowSource TfSource() => nes != null ? TimeflowSource.For(nes, () => cores) : TimeflowSource.For(cs!, () => cores);

    private TimeflowOptions TfOptions()
    {
        long now = Interlocked.Read(ref framesStarted);
        var o = new TimeflowOptions { Rom = romFile, RomName = romFileName, Cores = cores, Source = "mcp", Note = $"MCP headless session {Id}", StartFrame = now, PowerOnStart = now == 0 && jumpedDir == null };
        o.Coverage = tfCoverage; o.Accesses = tfAccesses;
        if (tfBranchParent != null)
        {
            // a branch: its frames are numbered from the parent's frame it starts at
            o.ParentId = TimelineMeta.Load(tfBranchParent).Id; o.ParentFrame = tfBranchFrame; o.StartFrame = tfBranchFrame + (now - jumpedAtSessionFrame);
            o.GameDir = TimeflowStore.GameDirOf(tfBranchParent);
        }
        return o;
    }

    private void TimeflowBefore(PadButtons p0, PadButtons p1)
    {
        if (!tf.On && tf.Current == null) return;
        tf.BeforeFrame(this, TfSource, TfOptions, p0, p1);
    }

    private JsonObject TfStatus()
    {
        var r = tf.Current;
        var j = new JsonObject
        {
            ["recording"] = tf.On, ["timeline"] = r?.Id, ["dir"] = r?.Dir, ["frames"] = r?.FramesCaptured, ["storedBytes"] = r?.StoredBytes,
            ["gameBytes"] = r?.GameBytes, ["capBytes"] = r?.CapBytes, ["lastEnd"] = tf.LastEnd.Length > 0 ? tf.LastEnd : null,
            ["refused"] = tf.Refused.Length > 0 ? tf.Refused : null, ["finished"] = J.Strs(tf.Finished.Select(Path.GetFileName).Select(x => x ?? "")),
        };
        return j;
    }

    public override JsonObject TimeflowRecord(string action, string? note)
    {
        switch (action)
        {
            case "start":
                if (cs != null && !cs.SupportsState) throw new ToolError($"Timeflow cannot record this session: {cs.StateUnsupportedReason}");
                tf.SwitchOn();
                var j = TfStatus();
                j["note"] = "recording this HEADLESS session (the agent's own machine) from its next frame: every frame's state (keyframes + page deltas) in BROKENDEV_HOME\\timeflow; a state load, reset or memory write ends the timeline there and a new one begins; stop with action stop. The user's own play is never recorded from here (a live session can only ask the user).";
                return j;
            case "stop":
            {
                EnsureBetweenFrames();
                var cur = tf.Current;
                tf.CloseNow("closed: switched off (timeflow_record stop)");
                tfBranchParent = null;
                var s = TfStatus();
                if (cur != null) s["closed"] = new JsonObject { ["id"] = cur.Id, ["dir"] = cur.Dir, ["frames"] = cur.FramesWritten, ["storedBytes"] = cur.StoredBytes, ["stopReason"] = cur.StopReason };
                return s;
            }
            case "status": return TfStatus();
            default: throw new ToolError("timeflow_record on a headless session: action start, stop or status");
        }
    }

    /// <summary>Puts this session at a frame of a timeline of the same ROM (the state restored, loaded, and proved to be the recorded one).</summary>
    public override JsonObject TimeflowJump(string timelineDir, long frame)
    {
        EnsureBetweenFrames();
        using var pb = new TimeflowPlayback(timelineDir);
        var src = TfSource();
        string sha = TimeflowStore.Sha256(romFile);
        try { pb.CheckMachine(src, sha); } catch (InvalidDataException e) { throw new ToolError(e.Message); }
        tf.Break($"the session jumped to timeline {pb.Meta.Id} frame {frame}");
        try { pb.Jump(src, frame); }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException) { throw new ToolError(e.Message); }
        FreshDebugState();
        try { StartHistory(SaveRaw()); } catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { history = null; historyNote = e.Message; }
        jumpedDir = pb.Dir; jumpedFrame = frame; jumpedAtSessionFrame = Interlocked.Read(ref framesStarted);
        tfBranchParent = pb.Dir; tfBranchFrame = frame;   // recorded from here, the session's frames are a branch of this timeline at this frame
        var row = pb.Reader.Row(frame);
        var ex = pb.Reader.Extras(frame);
        var j = Status();
        j["timeline"] = pb.Meta.Id; j["timelineFrame"] = frame; j["state"] = $"{row.State:x16}";
        j["frameInput"] = $"{(PadButtons)row.P1} / {(PadButtons)row.P2}";
        if (pb.Meta.Vars.Count > 0) j["vars"] = new JsonObject(pb.Meta.Vars.Select((v, i) => KeyValuePair.Create(v.Split('=')[0], (JsonNode?)(i < ex.Vars.Length ? ex.Vars[i] : null))));
        if (ex.Findings.Count > 0) j["findings"] = J.Strs(ex.Findings);
        j["note"] = "the session holds exactly the recorded state of that frame (checked). timeflow_resume mode replay runs the recorded inputs on (checked against the recording); mode live (or run / input) plays new input: a branch";
        return j;
    }

    public override JsonObject TimeflowResume(string mode, long frames, bool record)
    {
        if (jumpedDir == null) throw new ToolError("resume follows a timeflow_jump (this session has not jumped to a timeline frame)");
        long since = Interlocked.Read(ref framesStarted) - jumpedAtSessionFrame;
        long at = jumpedFrame + since;
        using var pb = new TimeflowPlayback(jumpedDir);
        if (mode == "replay")
        {
            if (since != 0) throw new ToolError($"replay continues from the frame jumped to; this session ran {since} frame(s) since: jump again first");
            long to = frames > 0 ? at + frames : pb.Reader.EndFrame;
            var src = TfSource();
            bool broke = false;
            var r = pb.Replay(src, at, to, (a, b) =>
            {
                lock (padLock) { held[0] = a; held[1] = b; schedule.Clear(); }
                if (RunFrames(1) != RunOutcome.Frames) broke = true;
            });
            var j = Status();
            j["mode"] = "replay"; j["timeline"] = pb.Meta.Id; j["from"] = at; j["framesRun"] = r.FramesRun; j["checks"] = r.Checked; j["inSync"] = r.InSync && !broke;
            if (r.OffFrame != null) j["offFrame"] = r.OffFrame; if (r.OffFrame != null) j["offWhat"] = r.OffWhat;
            if (broke) j["note"] = "a breakpoint or watch stopped the replay (the session is parked there)";
            jumpedFrame = at + r.FramesRun; jumpedAtSessionFrame = Interlocked.Read(ref framesStarted);
            return j;
        }
        if (mode != "live") throw new ToolError("mode is replay (the recorded inputs) or live (new input: a branch)");
        if (record) tf.SwitchOn();
        var o = RunFrames(Math.Max(0, frames));
        var jl = RunResult(o, Interlocked.Read(ref framesStarted));
        jl["mode"] = "live"; jl["timeline"] = pb.Meta.Id; jl["from"] = at;
        jl["note"] = record ? "a branch: this session's new input, recorded as a timeline whose parent is the jumped-to timeline at that frame (timeflow_record stop ends it)" : "a branch, not recorded (record:true keeps it as a timeline)";
        jl["timeflow"] = TfStatus();
        return jl;
    }

    public JsonObject TimeflowState() => TfStatus();
}
