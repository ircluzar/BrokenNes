namespace NesEmulator
{
// Mapper 66 (GxROM / MxROM): one latch at $8000-$FFFF, bits 4-5 = 32KB PRG bank, bits 0-1 = 8KB CHR bank.
// Fixed header mirroring, no PRG-RAM. Bus conflicts are modelled (the ROM byte ANDs the written value).
public sealed class Mapper66 : RegisterMapperBase
{
    public Mapper66(Cartridge c) : base(c, 1) { }
    protected override bool PrgRamPresent => false;
    protected override void Apply() { SetPrg32((r[0] >> 4) & 3); SetChr8(r[0] & 3); }
    public override void CPUWrite(ushort address, byte value)
    {
        if (address < 0x8000) return;
        r[0] = value & CPURead(address); Apply();
    }
}
}
