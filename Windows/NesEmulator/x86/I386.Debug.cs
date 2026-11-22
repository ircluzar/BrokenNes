// The x86 PC's debugger hooks (BrokenDev MCP: BrokenDev/mcp/HeadlessSession.X86.cs). Our own addition, not in tiny386.
// Off (Debug == null) the core runs exactly as before: one null test per Step batch, per port access and per interrupt; Exec1 and the
// compiled blocks are untouched. On, Step runs Exec1Dbg: every instruction interpreted (no compiled blocks), tested against the exec
// breakpoints and the step budget, and recorded in the trace ring. A stop ends the batch at an instruction boundary; PcMachine.RunFrame
// returns early (the frame is not finished: the next RunFrame carries on in it).

namespace NesEmulator.X86;

/// <summary>One stop reason / log line of the x86 debugger.</summary>
public readonly record struct X86DebugEvent(long Cycle, uint Cs, uint Eip, string Kind, int Number, uint Value, bool Write, int Bits);

/// <summary>The x86 debugger's state, hung on <see cref="I386.Debug"/> while debugging.</summary>
public sealed class X86Debug
{
    /// <summary>Exec breakpoints, by linear address (CS base + EIP).</summary>
    public readonly HashSet<uint> Exec = new();
    /// <summary>Port breakpoints: bit 0 = read (IN), bit 1 = write (OUT), per port.</summary>
    public readonly byte[] Ports = new byte[65536];
    public int PortCount;
    /// <summary>Software INT / IRQ / exception breakpoints by vector: bit 0 software INT n, bit 1 hardware IRQ, bit 2 CPU exception.</summary>
    public readonly byte[] Vectors = new byte[256];
    public int VectorCount;
    /// <summary>When positive: stop after this many more instructions.</summary>
    public long StepBudget;
    /// <summary>Stopped: the batch ended at an instruction boundary; PcMachine.RunFrame returns at once until it is cleared.</summary>
    public bool Stop;
    public string Reason = "";
    /// <summary>The stop's event (breakpoint, port access, interrupt).</summary>
    public X86DebugEvent StopEvent;
    /// <summary>The linear address an exec breakpoint may not stop at on the next instruction (resuming from it).</summary>
    public uint SkipLin = 0xffffffff;

    // the trace ring: (CS selector, EIP, linear) of the last instructions run while debugging
    public readonly uint[] TrCs, TrEip, TrLin;
    public readonly byte[] TrMode;
    public long TrCount;

    /// <summary>The port I/O log and the interrupt log (ring buffers; on when <see cref="LogPorts"/> / <see cref="LogInts"/>).</summary>
    public bool LogPorts, LogInts;
    public readonly Queue<X86DebugEvent> PortLog = new(), IntLog = new();
    public int LogCap = 4096;
    public long PortEvents, IntEvents;

    public X86Debug(int traceRing = 65536)
    {
        TrCs = new uint[traceRing]; TrEip = new uint[traceRing]; TrLin = new uint[traceRing]; TrMode = new byte[traceRing];
    }

    internal void Port(I386 c, int port, bool write, uint v, int bits)
    {
        if (LogPorts) { PortEvents++; if (PortLog.Count >= LogCap) PortLog.Dequeue(); PortLog.Enqueue(new(c.Cycle, c.SegSel[I386.SEG_CS], c.Ip, write ? "out" : "in", port, v, write, bits)); }
        if (PortCount > 0 && (Ports[port & 0xffff] & (write ? 2 : 1)) != 0 && !Stop)
        {
            Stop = true; Reason = write ? "port write" : "port read";
            StopEvent = new(c.Cycle, c.SegSel[I386.SEG_CS], c.Ip, write ? "out" : "in", port, v, write, bits);
        }
    }

    /// <summary>kind: 0 software INT n, 1 hardware IRQ, 2 CPU exception. Called before delivery (CS:EIP the instruction / the interrupted place).</summary>
    internal void Int(I386 c, int vector, int kind, uint err)
    {
        string k = kind == 0 ? "int" : kind == 1 ? "irq" : "exception";
        if (LogInts) { IntEvents++; if (IntLog.Count >= LogCap) IntLog.Dequeue(); IntLog.Enqueue(new(c.Cycle, c.SegSel[I386.SEG_CS], c.Ip, k, vector, kind == 0 ? c.R[0] : err, false, 0)); }
        if (VectorCount > 0 && (Vectors[vector & 0xff] & (1 << kind)) != 0 && !Stop)
        {
            Stop = true; Reason = k;
            StopEvent = new(c.Cycle, c.SegSel[I386.SEG_CS], c.Ip, k, vector, kind == 0 ? c.R[0] : err, false, 0);
        }
    }
}

public sealed partial class I386
{
    /// <summary>The debugger (null: off, the core as before). Set between frames.</summary>
    public X86Debug? Debug;

    /// <summary>Exec1 with the debugger on: interpreted, per instruction: exec breakpoints, the step budget, the trace ring.</summary>
    private bool Exec1Dbg(int stepcount)
    {
        var d = Debug!;
        _steps = stepcount;
        int mask = d.TrCs.Length;
        for (; _steps > 0; _steps--)
        {
            if (d.Stop) return true;
            uint lin = SegBase[SEG_CS] + NextIp;
            if (d.Exec.Count > 0 && lin != d.SkipLin && d.Exec.Contains(lin))
            {
                d.Stop = true; d.Reason = "breakpoint"; d.StopEvent = new(Cycle, SegSel[SEG_CS], NextIp, "exec", 0, lin, false, 0);
                return true;
            }
            d.SkipLin = 0xffffffff;
            int t = (int)(d.TrCount++ % mask);
            d.TrCs[t] = SegSel[SEG_CS]; d.TrEip[t] = NextIp; d.TrLin[t] = lin;
            d.TrMode[t] = (byte)((Cr0 & 1) == 0 ? 0 : (Flags & VM) != 0 ? 1 : Code16 ? 2 : 3);
            int r = Step1(false);
            if (d.StepBudget > 0 && --d.StepBudget == 0 && !d.Stop)
            {
                d.Stop = true; d.Reason = "step"; d.StopEvent = new(Cycle, SegSel[SEG_CS], NextIp, "step", 0, SegBase[SEG_CS] + NextIp, false, 0);
                if (r == FAIL) return false;   // (the fault is delivered first: the stop is at the handler's first instruction)
                return true;
            }
            if (r == OK) continue;
            if (r == FAIL) return false;
            return true;
        }
        return true;
    }
}
