namespace NesEmulator
{
// Mapper 71 (Camerica BF9093/BF9097, Codemasters): UNROM-like. $C000-$FFFF selects the 16KB bank at
// $8000 ($C000 fixed to the last bank); CHR is 8KB RAM. The BF9097 (Fire Hawk, submapper 1) also
// takes a one-screen select - as Mesen 2.1.1's BF909x: a write to exactly $9000 turns BF9097 mode on, then
// every $8000-$BFFF write sets one-screen mirroring, bit 4 set = screen A ($2000), clear = B; before that, $8000-$BFFF
// writes bank PRG like $C000. The BF9093 games keep their header mirroring. (nes-fix r4: Fire Hawk had A/B swapped and
// ignored $8000-$8FFF / $A000-$BFFF: its nametables went to the wrong page from frame 297.)
public sealed class Mapper71 : RegisterMapperBase
{
    public Mapper71(Cartridge c) : base(c, 3) { }   // r0 PRG bank, r1 mirroring (0 = header, 2/3 = one-screen), r2 BF9097 mode
    protected override bool PrgRamPresent => false;
    protected override void Apply()
    {
        SetPrg16(0, r[0]); SetPrg16(1, PrgLen / 0x4000 - 1); SetChr8(0);
        if (r[1] >= 2) Mirror(r[1]);
    }
    public override void CPUWrite(ushort address, byte value)
    {
        if (address < 0x8000) return;
        if (address == 0x9000) r[2] = 1;
        if (address >= 0xC000 || r[2] == 0) { r[0] = value; Apply(); }
        else { r[1] = (value & 0x10) != 0 ? 2 : 3; Apply(); }
    }
}
}
