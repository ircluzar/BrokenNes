namespace NesEmulator
{
// Mapper 113: NINA-03/06 multicart variant (Sachen / HES / Mojon). Register at $4100-$5FFF where (A & $4100) == $4100:
// [MCPP PCCC]: 32KB PRG = P, 8KB CHR = C | (high C << 3), M = 1 vertical / 0 horizontal mirroring.
public class Mapper113 : DiscreteMapperBase, ILowRegisterMapper
{
    public Mapper113(Cartridge c) : base(c) { }
    protected override bool HasPrgRam => false;
    protected override void ResetBoard() { }
    protected override void WriteRegister(ushort address, byte value) { }
    protected override void WriteLow(ushort address, byte value)
    {
        if (address >= 0x6000 || (address & 0x4100) != 0x4100) return;
        SetPrg32((value >> 3) & 0x07);
        SetChr8((value & 0x07) | ((value >> 3) & 0x08));
        SetMirror((value & 0x80) != 0 ? Mirroring.Vertical : Mirroring.Horizontal);
    }
}
}
