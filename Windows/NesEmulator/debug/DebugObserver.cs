namespace NesEmulator.Debugging;

/// <summary>
/// A tool that watches what the debugger's hooks see, without breaking: the hardware checks and the profiler of milestone D4
/// (Windows/NesEmulator/debug/checks). It is added to a <see cref="Debugger"/>, which then arms exactly the hooks it asks for and calls it
/// before its own breakpoint matching. With no observer (and nothing else set) the hooks stay null and the machine runs as without a
/// debugger.
/// </summary>
public abstract class DebugObserver
{
    /// <summary>Wants a call before every step of this CPU (by index).</summary>
    public virtual bool WantsSteps(int cpuIndex) => false;
    /// <summary>Wants every memory access of this CPU and its DMA (by index).</summary>
    public virtual bool WantsAccesses(int cpuIndex) => false;

    /// <summary>Before a step: an instruction at the CPU's PC (<paramref name="executes"/>), an interrupt entry (<paramref name="interrupt"/>
    /// names it) or a halted cycle. The call stack is already up to date for the CPU's PC.</summary>
    public virtual void OnStep(DebugCpu cpu, long cycle, bool executes, string? interrupt) { }

    /// <summary>A memory access, at the moment the debugger's watches see it: reads after they happen; writes before they land on the
    /// SNES, Game Boy and the Sega consoles (the picture ports still say where they go), after they land on the NES. A Z80 I/O port access (Master System, Game Gear,
    /// the Genesis's Z80) comes at $10000 + the port (on the Master System and Game Gear the port the console decodes: $BE for any mirror of the data port).</summary>
    public virtual void OnAccess(DebugCpu cpu, uint address, byte value, bool write, AccessSource source, int channel) { }

    /// <summary>A CPU write after it landed (the SNES and Game Boy CPUs, whose watching bus reports a write before it lands too).</summary>
    public virtual void OnWriteDone(DebugCpu cpu, uint address, byte value) { }

    /// <summary>An internal (no bus access) CPU cycle of the SNES 65816: with the accesses, the CPU's exact cycle count.</summary>
    public virtual void OnIdleCycle(DebugCpu cpu) { }

    /// <summary>A call frame was pushed (a call instruction or an interrupt entry, seen at the callee's first step).</summary>
    public virtual void OnCall(DebugCpu cpu, CallFrame frame) { }

    /// <summary>A call frame was popped (its return seen at the step after it).</summary>
    public virtual void OnReturn(DebugCpu cpu, CallFrame frame) { }

    /// <summary>The NES: a DMC sample fetch halted the CPU on its read of <paramref name="address"/> (the precise-DMA model of the FIX cores).</summary>
    public virtual void OnDmcHalt(DebugCpu cpu, uint address) { }
}
