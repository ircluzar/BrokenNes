namespace NesEmulator
{
// Mapper 34: two unrelated boards share the number.
//  - BNROM (Deadly Towers; CHR-RAM): $8000-$FFFF selects the 32KB PRG bank (bus conflicts).
//  - NINA-001 (Impossible Mission II; CHR-ROM): $7FFD = 32KB PRG bank, $7FFE / $7FFF = 4KB CHR banks
//    at $0000 / $1000; the registers sit on top of the 8KB PRG-RAM (writes reach both).
// Told apart by CHR-ROM presence, as the NES 2.0 submapper would (1 = NINA-001, 2 = BNROM).
public sealed class Mapper34 : RegisterMapperBase
{
    public Mapper34(Cartridge c) : base(c, 3) { }   // r0 PRG, r1 CHR $0000 (NINA), r2 CHR $1000 (NINA)
    private bool Nina => cart.chrBanks > 0;
    protected override bool PrgRamPresent => Nina;
    protected override void PowerOn() { if (Nina) r[2] = 1; }
    protected override void Apply()
    {
        SetPrg32(r[0]);
        if (Nina) { SetChr4(0, r[1]); SetChr4(1, r[2]); } else SetChr8(0);
    }
    public override void CPUWrite(ushort address, byte value)
    {
        if (Nina)
        {
            if (address >= 0x6000 && address < 0x8000)
            {
                WriteRam(address, value);
                switch (address) { case 0x7FFD: r[0] = value & 1; Apply(); break; case 0x7FFE: r[1] = value & 0xF; Apply(); break; case 0x7FFF: r[2] = value & 0xF; Apply(); break; }
            }
            return;
        }
        if (address >= 0x8000) { r[0] = value & CPURead(address); Apply(); }
    }
}
}
