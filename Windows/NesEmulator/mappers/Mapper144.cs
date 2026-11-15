namespace NesEmulator
{
// Mapper 144: AGCI 50282 (Death Race), a Color Dreams variant. Same register as mapper 11, but only the ROM drives
// D0 during the write (value & (rom | 1)), and a write to exactly $8000 is ignored - Mesen 2's ColorDreams board
// does both; with plain mapper-11 behaviour Death Race jumps into garbage on its 14th frame.
public class Mapper144 : DiscreteMapperBase
{
    public Mapper144(Cartridge c) : base(c) { }
    protected override void ResetBoard() { }
    public override void CPUWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            if (address == 0x8000) return;
            value &= (byte)(cart.prgROM[PrgIndex(address)] | 1);
            WriteRegister(address, value);
            return;
        }
        base.CPUWrite(address, value);
    }
    protected override void WriteRegister(ushort address, byte value) { SetPrg32(value & 0x03); SetChr8(value >> 4); }
}
}
