namespace NesEmulator
{
// Mapper 67: Sunsoft-3 (Fantasy Zone II, Mito Koumon). Registers decode A15-A11 = 1xxx1:
// $8800/$9800/$A800/$B800 2KB CHR; $C800 IRQ counter (high byte first, then low, alternating);
// $D800 [...E ....] IRQ enable, also resets the write toggle and acknowledges; $E800 mirroring 0 V/1 H/2 A/3 B;
// $F800 16KB PRG at $8000, $C000 fixed to last. The 16-bit counter counts down every CPU cycle while
// enabled; going $0000 -> $FFFF raises the IRQ and disables counting.
public class Mapper67 : DiscreteMapperBase, ICpuClockedMapper
{
    private int counter, irqEnabled, toggle, irqPending;
    public Mapper67(Cartridge c) : base(c) { }
    protected override void ResetBoard() { counter = 0; irqEnabled = 0; toggle = 0; irqPending = 0; SetPrg16(0, 0); SetPrg16(1, LastPrg16); }
    protected override void WriteRegister(ushort address, byte value)
    {
        if ((address & 0x0800) == 0) return;
        switch (address & 0xF000)
        {
            case 0x8000: SetChr2(0, value); break;
            case 0x9000: SetChr2(1, value); break;
            case 0xA000: SetChr2(2, value); break;
            case 0xB000: SetChr2(3, value); break;
            case 0xC000:
                if (toggle == 0) counter = (counter & 0x00FF) | (value << 8); else counter = (counter & 0xFF00) | value;
                toggle ^= 1; break;
            case 0xD000: irqEnabled = (value >> 4) & 1; toggle = 0; irqPending = 0; break;
            case 0xE000: SetMirror((value & 3) switch { 0 => Mirroring.Vertical, 1 => Mirroring.Horizontal, 2 => Mirroring.SingleScreenA, _ => Mirroring.SingleScreenB }); break;
            case 0xF000: SetPrg16(0, value & 0x0F); break;
        }
    }
    public bool IrqLine => irqPending != 0;
    public void CpuClock(int cpuCycles)
    {
        if (irqEnabled == 0) return;
        if (counter >= cpuCycles) { counter -= cpuCycles; return; }
        counter = (counter - cpuCycles) & 0xFFFF; irqEnabled = 0; irqPending = 1;
    }
    protected override int[] ExtraState { get => new[] { counter, irqEnabled, toggle, irqPending }; set { if (value.Length >= 3) { counter = value[0]; irqEnabled = value[1]; toggle = value[2]; irqPending = value.Length > 3 ? value[3] : 0; } } }
}
}
