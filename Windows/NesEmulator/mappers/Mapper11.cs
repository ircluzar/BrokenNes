namespace NesEmulator
{
// Mapper 11: Color Dreams (and Wisdom Tree). $8000-$FFFF [CCCC ..PP]: 32KB PRG = P, 8KB CHR = C. Bus conflicts.
public class Mapper11 : DiscreteMapperBase
{
    public Mapper11(Cartridge c) : base(c) { }
    protected override bool HasBusConflicts => true;
    protected override void ResetBoard() { }
    protected override void WriteRegister(ushort address, byte value) { SetPrg32(value & 0x03); SetChr8(value >> 4); }
}
}
