using System;
using System.Collections.Generic;
using System.Threading;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>
/// The recording switch of one host (the desktop app, an MCP headless session): OFF until switched on, and while on it keeps one timeline
/// recording the running game. Something outside the game (a state load, a reset, a memory write, a core swap, another game) ends the
/// current timeline there, saying why, and the next frame begins a new one: a timeline only ever holds frames that replay into each other.
/// Off, <see cref="BeforeFrame"/> and <see cref="AfterFrame"/> return at their first test and nothing is written.
/// <para>The host calls <see cref="BeforeFrame"/> and <see cref="AfterFrame"/> on the emulation thread around each frame;
/// <see cref="SwitchOn"/>, <see cref="SwitchOff"/> and <see cref="Break"/> from any thread (they apply before the next frame).</para>
/// </summary>
public sealed class TimeflowSwitch
{
    private volatile bool on;
    private volatile bool stopWanted;
    private string? pendingBreak;
    private object? recordingFor, refusedFor;
    private TimeflowRecorder? rec;
    private Func<TimeflowSource>? source;
    private Func<TimeflowOptions>? options;
    private readonly List<string> finished = new();

    /// <summary>Recording is switched on (a timeline may not have started yet: it starts at the next frame of a game).</summary>
    public bool On => on;
    public TimeflowRecorder? Current => rec;
    /// <summary>The last timeline's end, in words ("closed: a state was loaded", or the reason it stopped by itself).</summary>
    public string LastEnd { get; private set; } = "";
    /// <summary>Why the switch could not record the running game (a machine without states, the cap with everything kept), or "".</summary>
    public string Refused { get; private set; } = "";
    /// <summary>The timelines this switch finished (their folders), oldest first.</summary>
    public IReadOnlyList<string> Finished { get { lock (finished) return finished.ToArray(); } }
    /// <summary>Raised (on the emulation thread or the writer's) when a timeline stops by itself (a failure): the host shows it.</summary>
    public event Action<string>? Stopped;

    /// <summary>Switches recording on (from the next frame). The factories make the running game's source and options when a timeline starts.</summary>
    public void SwitchOn() { Refused = ""; stopWanted = false; on = true; }

    /// <summary>Switches recording off: the current timeline is closed before the next frame (or now, with <see cref="CloseNow"/>).</summary>
    public void SwitchOff() { on = false; stopWanted = true; }

    /// <summary>Something outside the game changed it: the current timeline ends before the next frame and a new one begins.</summary>
    public void Break(string why) { if (on) Interlocked.Exchange(ref pendingBreak, why); }

    /// <summary>
    /// Before a frame (emulation thread, between frames). <paramref name="machine"/> identifies the running game (the NES object, the session):
    /// another one ends the timeline. The factories are only called when a timeline starts.
    /// </summary>
    public void BeforeFrame(object machine, Func<TimeflowSource> makeSource, Func<TimeflowOptions> makeOptions, NesEmulator.Systems.PadButtons p1, NesEmulator.Systems.PadButtons p2)
    {
        if (!on && rec == null && !stopWanted) return;
        if (stopWanted && !on) { End("closed: recording was switched off"); stopWanted = false; return; }
        if (Interlocked.Exchange(ref pendingBreak, null) is { } why && rec != null) End($"closed: {why}");
        if (rec != null && !ReferenceEquals(recordingFor, machine)) End("closed: another game was started");
        if (rec != null && !rec.Running) End($"stopped: {rec.StopReason}");
        if (!on) return;
        if (rec == null)
        {
            if (Refused.Length > 0 && ReferenceEquals(refusedFor, machine)) return;
            try
            {
                source = makeSource; options = makeOptions;
                rec = TimeflowRecorder.Start(makeSource(), makeOptions());
                recordingFor = machine;
                rec.Stopped += r => Stopped?.Invoke(r);
            }
            catch (Exception e) when (e is NotSupportedException or System.IO.IOException or InvalidOperationException or ArgumentException or System.IO.InvalidDataException or UnauthorizedAccessException)
            {
                Refused = e.Message; refusedFor = machine;
                Stopped?.Invoke(e.Message);
                return;
            }
        }
        rec.BeforeFrame(p1, p2);
    }

    public void AfterFrame() { rec?.AfterFrame(); }

    private void End(string how)
    {
        var r = rec;
        rec = null; recordingFor = null;
        if (r == null) return;
        string dir = r.Stop();
        LastEnd = r.StopReason != null ? $"stopped: {r.StopReason}" : how;
        lock (finished) finished.Add(dir);
    }

    /// <summary>Closes the current timeline now (the host is closing, or switched off between frames on its own thread).</summary>
    public void CloseNow(string why = "closed: the game was closed")
    {
        on = false; stopWanted = false;
        End(why);
    }
}
