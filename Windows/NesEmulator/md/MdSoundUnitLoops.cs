using System;

namespace NesEmulator.Sega;

/// <summary>
/// Polling loops of the Genesis sound CPU, fast-forwarded. A Z80 sound driver spends most of its time waiting for the 68000 to give it a command: <c>LD A,(HL) / OR A / JP P,loop</c> on a byte of its own RAM,
/// forever. Measured on real games, 53 to 99 percent of the Z80's T-states are in a loop of three to five instructions (Sonic the Hedgehog, Sonic 2, Streets of Rage, Gunstar Heroes), and each T-state costs a
/// pass through the micro-operation engine. Such a loop is a function of the registers and of the bytes it reads, and nothing else changes either until the 68000 writes the Z80's RAM, an interrupt arrives, or the
/// bus is taken: so once the CPU has been seen to come back to exactly the state it left (every register but the low seven bits of R, the flip-flops, the latches) with only reads of its own RAM in between, the
/// turns are counted instead of run, and the CPU is brought up to date (whole turns skipped, the remainder of the last one ticked for real) before anything can see or change it.
/// </summary>
/// <remarks>Exactness: the machine ends up in the state a tick-by-tick run would have left it in at every moment anything can look (the 68000's accesses, the interrupt line, the bus request and reset, save states, tools);
/// the speed suite's goldens (every frame and sample of six games) and a state-hash comparison of the twin (<c>MdSoundUnit.FastLoops = false</c>) every 50 frames show it. Anything impure in the observed turn (a read outside the RAM,
/// any write, any port) means no fast-forward.</remarks>
public partial class MdSoundUnit
{
    /// <summary>True: polling loops are fast-forwarded as described above. False: every T-state is ticked, the reference twin (<c>--sega-bench --ab md-loop</c>; the output is bit for bit the same either way).</summary>
    public static bool FastLoops = true;

    private readonly Z80<ZBus>.LoopMark loopMark = new();
    private bool parked;                    // the CPU stands at the loop's head state and the turns since are only counted
    private long parkTicks;                 // T-states counted since it was parked
    private long loopTicks;                 // T-states in one turn of the loop
    private int loopRefresh;                // what a turn adds to the refresh counter
    private int loopState;                  // 0 scanning for a repeating instruction address, 1 recording one turn
    private ushort loopHead;
    private long loopStartTick, loopQuietUntil;
    /// <summary>How many times a loop was parked, and how many T-states were counted instead of ticked (a diagnostic: the share of the Z80's time the fast-forward took out).</summary>
    public long LoopParks, LoopParkedTicks;
    internal bool loopImpure;               // the turn being recorded touched something other than the Z80's RAM
    private readonly ushort[] seenPc = new ushort[1024];
    private readonly long[] seenTick = new long[1024];

    /// <summary>Called at an instruction boundary inside the Z80's run: true when the CPU has just been parked (the caller then counts the rest of its T-states instead of ticking them).</summary>
    private bool LoopStep()
    {
        ushort pc = z80.PC;
        long now = Z80Ticks;
        if (loopState == 0)
        {
            int slot = pc & 1023;
            bool repeat = seenPc[slot] == pc && seenTick[slot] != 0 && now - seenTick[slot] <= 600;
            seenPc[slot] = pc; seenTick[slot] = now;
            if (repeat && now >= loopQuietUntil && stallTicks == 0 && !z80.Irq)
            {
                z80.CaptureLoopMark(loopMark);
                loopHead = pc; loopStartTick = now; loopImpure = false; loopState = 1;
            }
            return false;
        }
        if (loopImpure || stallTicks != 0) { StopRecording(now); return false; }
        if (pc == loopHead)
        {
            long turn = now - loopStartTick;
            if (turn > 0 && turn <= 4000 && !z80.Irq && z80.MatchesLoopMark(loopMark))
            {
                loopTicks = turn; loopRefresh = z80.RefreshSince(loopMark);
                parked = true; parkTicks = 0; loopState = 0; LoopParks++;
                return true;
            }
            StopRecording(now);
            return false;
        }
        if (now - loopStartTick > 4000) StopRecording(now);
        return false;
    }

    private void StopRecording(long now) { loopState = 0; loopQuietUntil = now + 256; }

    /// <summary>The Z80 is parked: its next <paramref name="n"/> T-states are counted (and its clock moves on), nothing more.</summary>
    private void ParkedAdvance(int n)
    {
        if (n <= 0) return;
        parkTicks += n; Z80Ticks += n; LoopParkedTicks += n;
        z80Master += (long)n * machine.Timeline.Z80.Divider;
    }

    /// <summary>Brings a parked CPU up to date: the whole turns are skipped, the part of the last one that has run is ticked for real from the loop's head state. Does nothing when it is not parked.</summary>
    private void Unpark()
    {
        if (!parked) return;
        parked = false;
        long turns = parkTicks / loopTicks;
        int rest = (int)(parkTicks - turns * loopTicks);
        parkTicks = 0;
        z80.SkipLoop(turns, loopTicks, loopRefresh);
        for (int i = 0; i < rest; i++) z80.Tick();      // reads of the Z80's own RAM, stamped with the clock as it stands (nothing looks at a RAM read's time)
        loopState = 0; loopQuietUntil = Z80Ticks + 64;
    }

    /// <summary>Anything that could change what a loop does (an interrupt, the bus, a reset) ends a fast-forward and spoils a turn being recorded.</summary>
    private void LoopBreak() { Unpark(); loopState = 0; }

    private void ResetLoops()
    {
        parked = false; parkTicks = 0; loopState = 0; loopQuietUntil = 0; loopImpure = false;
        Array.Clear(seenTick);
    }
}
