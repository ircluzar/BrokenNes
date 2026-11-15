namespace NesEmulator
{
// Mapper 41 (Caltron 6-in-1). Outer register: a write to $6000-$67FF latches the ADDRESS:
//   A0-A2 = 32KB PRG bank, A3-A4 = CHR bank bits 2-3, A5 = mirroring (1 horizontal, 0 vertical).
// Inner register: $8000-$FFFF bits 0-1 = CHR bank bits 0-1, accepted only while outer A2 is set.
public sealed class Mapper41 : RegisterMapperBase
{
    public Mapper41(Cartridge c) : base(c, 2) { }   // r0 outer (address bits), r1 inner CHR
    protected override bool PrgRamPresent => false;
    protected override void Apply()
    {
        SetPrg32(r[0] & 7);
        SetChr8(((r[0] >> 1) & 0x0C) | (r[1] & 3));
        Mirror((r[0] & 0x20) != 0 ? 1 : 0);
    }
    public override void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address < 0x6800) { r[0] = address & 0x3F; Apply(); }
        else if (address >= 0x8000 && (r[0] & 4) != 0) { r[1] = value & 3; Apply(); }
    }
}
}
