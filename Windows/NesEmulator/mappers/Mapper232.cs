namespace NesEmulator
{
// Mapper 232: Camerica BF9096 (Quattro multicarts). $8000-$BFFF [...B B...]: outer 64KB block;
// $C000-$FFFF [.... ..PP]: inner 16KB page. $8000 = block*4 + page, $C000 = block*4 + 3. CHR-RAM.
public class Mapper232 : DiscreteMapperBase
{
    private int block, page;
    public Mapper232(Cartridge c) : base(c) { }
    protected override void ResetBoard() { block = 0; page = 0; Apply(); }
    private void Apply() { SetPrg16(0, (block << 2) | page); SetPrg16(1, (block << 2) | 3); }
    protected override void WriteRegister(ushort address, byte value)
    {
        if (address < 0xC000) block = (value >> 3) & 0x03; else page = value & 0x03;
        Apply();
    }
    protected override int[] ExtraState { get => new[] { block, page }; set { if (value.Length == 2) { block = value[0]; page = value[1]; } } }
}
}
