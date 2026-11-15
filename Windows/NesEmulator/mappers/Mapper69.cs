namespace NesEmulator
{
// Mapper 69 (Sunsoft FME-7 / 5A / 5B; Batman: Return of the Joker, Gimmick!, Hebereke).
//   $8000-$9FFF command (low 4 bits), $A000-$BFFF parameter:
//     0-7  1KB CHR banks
//     8    $6000-$7FFF: bit 6 = RAM (else ROM bank bits 0-5), bit 7 = RAM enabled
//     9-B  8KB PRG at $8000 / $A000 / $C000 ($E000 fixed to the last bank)
//     C    mirroring (0 V, 1 H, 2 one-screen A, 3 one-screen B)
//     D    IRQ control: bit 0 IRQ enable, bit 7 counter enable; any write acknowledges
//     E/F  counter low / high
//   The counter decrements every CPU cycle while enabled; 0 -> $FFFF raises the IRQ if enabled.
// The 5B's expansion sound ($C000/$E000) is not emulated: Gimmick! plays without its extra channels.
public sealed class Mapper69 : RegisterMapperBase, ICpuClockedMapper
{
    // r0-7 CHR, r8 $6000 reg, r9-11 PRG, r12 mirroring, r13 irq ctl, r14 counter, r15 command, r16 irq line
    public Mapper69(Cartridge c) : base(c, 17) { }
    private bool RamAt6000 => (r[8] & 0x40) != 0;
    protected override bool PrgRamPresent => RamAt6000;
    private int lowPrg;
    protected override void Apply()
    {
        for (int i = 0; i < 8; i++) SetChr1(i, r[i]);
        SetPrg8(0, r[9]); SetPrg8(1, r[10]); SetPrg8(2, r[11]); SetPrg8(3, LastPrg8);
        int n = System.Math.Max(1, PrgLen / 0x2000); lowPrg = ((r[8] & 0x3F) % n) * 0x2000;
        Mirror(r[12] & 3);
    }
    protected override void PowerOn() { r[12] = cart.mirroringMode == Mirroring.Vertical ? 0 : 1; }
    public override bool IsCpuReadOpenBus(ushort address) => address < 0x6000;
    public override byte CPURead(ushort address)
    {
        if (address >= 0x6000 && address < 0x8000)
        {
            if (!RamAt6000) return cart.prgROM[(lowPrg + (address & 0x1FFF)) % PrgLen];
            return (r[8] & 0x80) != 0 ? cart.prgRAM[address - 0x6000] : (byte)0;
        }
        return base.CPURead(address);
    }
    public override void CPUWrite(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            if (address >= 0x6000 && RamAt6000 && (r[8] & 0x80) != 0) cart.prgRAM[address - 0x6000] = value;
            return;
        }
        if (address < 0xA000) { r[15] = value & 0x0F; return; }
        if (address >= 0xC000) return;   // 5B audio ports: not emulated
        int cmd = r[15];
        switch (cmd)
        {
            case 0xD: r[13] = value & 0x81; r[16] = 0; return;
            case 0xE: r[14] = (r[14] & 0xFF00) | value; return;
            case 0xF: r[14] = (r[14] & 0x00FF) | (value << 8); return;
            case 0x8: r[8] = value; break;
            case 0xC: r[12] = value & 3; break;
            default:
                if (cmd < 8) r[cmd] = value; else r[cmd] = value & 0x3F;   // 9-B
                break;
        }
        Apply();
    }
    public void CpuClock(int cpuCycles)
    {
        if ((r[13] & 0x80) == 0) return;
        for (int i = 0; i < cpuCycles; i++)
        {
            r[14] = (r[14] - 1) & 0xFFFF;
            if (r[14] == 0xFFFF && (r[13] & 1) != 0) r[16] = 1;
        }
    }
    public bool IrqLine => r[16] != 0;
}
}
