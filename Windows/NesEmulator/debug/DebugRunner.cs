using System;
using System.Threading;
using NesEmulator.Gb;
using NesEmulator.Systems;

namespace NesEmulator.Debugging;

/// <summary>Makes the debugger's view of a running machine.</summary>
public static class DebugMachines
{
    public static DebugMachine For(NES nes) => new NesDebugMachine(nes);

    /// <summary>A SNES / WideMess, Game Boy, Master System / Game Gear or Genesis session (D1, and T0 for the Sega consoles); anything else is refused with the reason.</summary>
    public static DebugMachine For(IConsoleSession session) => session switch
    {
        SnesSession snes => new SnesDebugMachine(snes),
        GbSession gb => new GbDebugMachine(gb),
        NesEmulator.Mix.SegaPictureSession w => For(w.Inner),
        NesEmulator.Sega.SmsSession sms => new SmsDebugMachine(sms),
        NesEmulator.Sega.MdSession md => new MdDebugMachine(md),
        _ => throw new NotSupportedException($"the debugger does not cover the {Consoles.DisplayName(session.Console)} yet (NES, SNES with its SPC700, WideMess, Game Boy, Master System, Game Gear, Genesis with its Z80)"),
    };
}

/// <summary>How a <see cref="DebugRunner.Run"/> ended.</summary>
public enum RunOutcome { Frames, Break, Timeout, Error }

/// <summary>
/// The headless driver: runs a machine's frames on its own thread so a breakpoint can park that thread mid-frame while the caller (a
/// Workshop command, the MCP server of D3) inspects the machine and steps it. Calls are synchronous: each returns when the machine has
/// run the frames asked for or has stopped at a break. Between calls the machine is still (parked or between frames), so reading it is safe.
/// </summary>
public sealed class DebugRunner : IDisposable
{
    private readonly Action runFrame;
    private readonly Thread worker;
    private readonly object gate = new();
    private long framesDone, framesTarget;
    private bool running, quit;
    public Debugger Debugger { get; }
    public Exception? Error { get; private set; }
    public long FramesDone { get { lock (gate) return framesDone; } }

    public DebugRunner(Debugger debugger, Action runFrame)
    {
        Debugger = debugger;
        this.runFrame = runFrame;
        debugger.Parked += _ => { lock (gate) Monitor.PulseAll(gate); };
        worker = new Thread(Loop) { IsBackground = true, Name = "BrokenNes debug emulation" };
        worker.Start();
    }

    private void Loop()
    {
        while (true)
        {
            lock (gate)
            {
                while (!quit && framesDone >= framesTarget) Monitor.Wait(gate);
                if (quit) return;
                running = true;
            }
            try { runFrame(); }
            catch (Exception e) { lock (gate) { Error = e; framesTarget = framesDone + 1; } }
            lock (gate) { framesDone++; running = false; Monitor.PulseAll(gate); }
        }
    }

    /// <summary>Runs until <paramref name="frames"/> more frames have finished (a frame stopped mid-way counts when it finishes), or the debugger breaks.</summary>
    public RunOutcome Run(long frames, int timeoutMs = 600_000) => Go(() => Debugger.Continue(), frames, timeoutMs);

    /// <summary>Issues a step (or run-to) command and waits for its break, giving up after <paramref name="guardFrames"/> frames.</summary>
    public RunOutcome Step(Action command, long guardFrames = 600, int timeoutMs = 600_000) => Go(command, guardFrames, timeoutMs);

    private RunOutcome Go(Action command, long frames, int timeoutMs)
    {
        long parksBefore = Debugger.ParkCount;
        command();   // arms the step and lets a parked machine go
        var end = Environment.TickCount64 + timeoutMs;
        lock (gate)
        {
            framesTarget = framesDone + Math.Max(1, frames);
            Monitor.PulseAll(gate);
            while (true)
            {
                if (Debugger.ParkCount != parksBefore && Debugger.IsParked) return RunOutcome.Break;
                if (Error != null) return RunOutcome.Error;
                if (framesDone >= framesTarget && !running) return RunOutcome.Frames;
                long left = end - Environment.TickCount64;
                if (left <= 0) return RunOutcome.Timeout;
                Monitor.Wait(gate, (int)Math.Min(left, 250));
            }
        }
    }

    /// <summary>Lets the machine go (no breakpoints, no hooks) and stops the thread once it is between frames.</summary>
    public void Dispose()
    {
        Debugger.Detach();
        lock (gate) { quit = true; Monitor.PulseAll(gate); }
        worker.Join(10_000);
    }
}
