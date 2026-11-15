namespace NesEmulator
{
// Mapper 66: GxROM / MHROM. $8000-$FFFF [..PP ..CC]: 32KB PRG = P, 8KB CHR = C. Bus conflicts.
public class Mapper66 : DiscreteMapperBase
{
    public Mapper66(Cartridge c) : base(c) { }
    protected override bool HasBusConflicts => true;
    protected override void ResetBoard() { }
    protected override void WriteRegister(ushort address, byte value) { SetPrg32((value >> 4) & 0x03); SetChr8(value & 0x03); }
}
}
