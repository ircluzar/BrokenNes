namespace NesEmulator
{
// Mapper 79: AVE NINA-03 / NINA-06. Register at $4100-$5FFF where (A & $E100) == $4100: [.... PCCC]
// 32KB PRG = P, 8KB CHR = C. Mirroring is soldered (header). No PRG-RAM.
public class Mapper79 : DiscreteMapperBase, ILowRegisterMapper
{
    public Mapper79(Cartridge c) : base(c) { }
    protected override bool HasPrgRam => false;
    protected override void ResetBoard() { }
    protected override void WriteRegister(ushort address, byte value) { }
    protected override void WriteLow(ushort address, byte value)
    {
        if ((address & 0xE100) == 0x4100) { SetPrg32((value >> 3) & 0x01); SetChr8(value & 0x07); }
    }
}
}
