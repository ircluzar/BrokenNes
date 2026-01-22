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
}
