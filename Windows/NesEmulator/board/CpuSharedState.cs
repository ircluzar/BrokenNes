namespace NesEmulator;

// Shared cross-core container so hot-swaps can transfer full CPU state reliably
public class CpuSharedState
{
    public byte A { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public byte status { get; set; }
    public ushort PC { get; set; }
    public ushort SP { get; set; }
    public bool irqRequested { get; set; }
    public bool nmiRequested { get; set; }
    public bool irqDeferOne { get; set; } // CPU_FIX: an IRQ that arrived in the last cycle waits one instruction
        public bool nmiDeferOne { get; set; } // CPU_FIX: likewise for an NMI that arrived too late in the instruction
    // CPU_FIX: the finished instruction's length as the interrupt polls judge a late interrupt (dispatched, irqPoll) and where in the running
    // instruction an interrupt was raised (-1 none). A state taken between frames still owes these to the next interrupt: left out, a load kept the
    // old timeline's (fuzz undo-mismatch). Absent in older states: 0 / -1, as at power-on.
    public int dispatchedCycles { get; set; }
    public int irqPollCycles { get; set; }
    public long irqRaisedRel { get; set; } = -1;
    public long nmiRaisedRel { get; set; } = -1;
}
