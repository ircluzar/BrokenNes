namespace NesEmulator
{
// Mapper 67 (Sunsoft-3; Fantasy Zone II JP, Mito Koumon). Registers decode on A11 set ($x800):
//   $8800/$9800/$A800/$B800 = 2KB CHR banks, $C800 = IRQ counter (two writes, high byte first),
//   $D800 = bit 4 IRQ/counter enable (also resets the write toggle and acknowledges),
//   $E800 = mirroring (0 V, 1 H, 2 one-screen A, 3 one-screen B), $F800 = 16KB PRG at $8000 ($C000 last).
// The 16-bit counter runs down every CPU cycle while enabled; going 0 -> $FFFF raises the IRQ and
// stops the counter (Mesen 2 / NESdev "Sunsoft 3"). 8KB PRG-RAM at $6000 (battery if the header says).
public sealed class Mapper67 : RegisterMapperBase, ICpuClockedMapper
{
    // r0-3 CHR, r4 PRG, r5 mirroring, r6 counter, r7 enable, r8 toggle, r9 irq line
    public Mapper67(Cartridge c) : base(c, 10) { }
    protected override void Apply()
    {
        for (int i = 0; i < 4; i++) SetChr2(i, r[i]);
        SetPrg16(0, r[4]); SetPrg16(1, PrgLen / 0x4000 - 1);
        Mirror(r[5] & 3);
    }
    protected override void PowerOn() { r[5] = cart.mirroringMode == Mirroring.Vertical ? 0 : 1; }
    public override void CPUWrite(ushort address, byte value)
    {
        if (address < 0x8000) { WriteRam(address, value); return; }
        switch (address & 0xF800)
        {
            case 0x8800: r[0] = value; break;
            case 0x9800: r[1] = value; break;
            case 0xA800: r[2] = value; break;
            case 0xB800: r[3] = value; break;
            case 0xC800:
                r[6] = r[8] == 0 ? (r[6] & 0x00FF) | (value << 8) : (r[6] & 0xFF00) | value;
                r[8] ^= 1; return;
            case 0xD800: r[7] = (value & 0x10) != 0 ? 1 : 0; r[8] = 0; r[9] = 0; return;
            case 0xE800: r[5] = value & 3; break;
            case 0xF800: r[4] = value; break;
            default: return;
        }
        Apply();
    }
    public void CpuClock(int cpuCycles)
    {
        if (r[7] == 0) return;
        for (int i = 0; i < cpuCycles; i++)
        {
            if (r[6] == 0) { r[6] = 0xFFFF; r[7] = 0; r[9] = 1; return; }
            r[6]--;
        }
    }
    public bool IrqLine => r[9] != 0;
}
}
