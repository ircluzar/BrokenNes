namespace NesEmulator
{
// Mapper 41: Caltron 6-in-1. Outer register = the ADDRESS of a write to $6000-$67FF: [..MH HEPP P]
// (A5 = mirroring 1 horizontal, A4-A3 = CHR outer bits, A2 = inner-CHR enable, A2-A0 = 32KB PRG).
// Inner register $8000-$FFFF (only while A2 was set): CHR inner = value & 3. No PRG-RAM.
public class Mapper41 : DiscreteMapperBase
{
    private int outer, inner;
    public Mapper41(Cartridge c) : base(c) { }
    protected override bool HasPrgRam => false;
    protected override void ResetBoard() { outer = 0; inner = 0; Apply(); }
    private void Apply()
    {
        SetPrg32(outer & 0x07);
        SetChr8(((outer >> 1) & 0x0C) | inner);
        SetMirror((outer & 0x20) != 0 ? Mirroring.Horizontal : Mirroring.Vertical);
    }
    protected override void WriteLow(ushort address, byte value)
    {
        if (address >= 0x6000 && address < 0x6800) { outer = address & 0x3F; Apply(); }
    }
    protected override void WriteRegister(ushort address, byte value)
    {
        if ((outer & 0x04) != 0) { inner = value & 0x03; Apply(); }
    }
    protected override int[] ExtraState { get => new[] { outer, inner }; set { if (value.Length == 2) { outer = value[0]; inner = value[1]; } } }
}
}
