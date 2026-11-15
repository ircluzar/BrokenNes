namespace NesEmulator
{
// Mapper 13: CPROM (Videomation). 32KB PRG fixed; 16KB CHR-RAM, $0000-$0FFF fixed to its first 4KB,
// $1000-$1FFF = 4KB page (value & 3) selected by any $8000-$FFFF write. Bus conflicts. Cartridge
// allocates the 16KB of CHR-RAM for this board. From the NESdev wiki / Mesen 2 CpRom.
public class Mapper13 : DiscreteMapperBase
{
    public Mapper13(Cartridge c) : base(c) { }
    protected override bool HasBusConflicts => true;
    protected override bool HasPrgRam => false;
    protected override void ResetBoard() { SetUpper(0); }
    protected override void WriteRegister(ushort address, byte value) { SetUpper(value & 0x03); }
    private void SetUpper(int page) { for (int i = 0; i < 4; i++) { chr1[i] = i; chr1[4 + i] = page * 4 + i; } }
}
}
